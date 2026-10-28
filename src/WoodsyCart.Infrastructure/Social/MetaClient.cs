using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace WoodsyCart.Infrastructure.Social;

public class MetaException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
    /// <summary>The token was revoked or expired (Meta error 190): reconnect in Admin.</summary>
    public bool TokenInvalid => Code == 190;
    /// <summary>The token lacks a permission (Meta error 10 or 200-299).</summary>
    public bool IsPermission => Code is 10 or (>= 200 and < 300);
}

public record MetaPage(string Id, string Name, string Token, string? InstagramId, string? InstagramUsername);
public record PublishResult(string Id, string? Permalink);
/// <summary>A post's numbers; null = the platform doesn't give it. NeedsPermission = some numbers need a permission not granted yet.</summary>
public record PostStats(int? Views, int? Reach, int? Likes, int? Comments, int? Shares, int? Saves, bool NeedsPermission);
/// <summary>A comment as the platform gives it. ParentId = the comment it answers (null = on the post itself).</summary>
public record FetchedComment(string Id, string? ParentId, string Author, string? AuthorId, string Text, DateTime At, bool? Mine);

/// <summary>
/// The few Graph API calls WoodsyCart needs: connect a Page (and its linked Instagram account), publish photo posts,
/// edit a Facebook post's text, delete posts and read Instagram's publishing limit.
/// </summary>
public class MetaClient(IHttpClientFactory http, IConfiguration config)
{
    private string Version => config["Social:GraphVersion"] ?? "v23.0";
    private string Base => $"https://graph.facebook.com/{Version}/";
    private HttpClient Client => http.CreateClient("meta");

    // ---------- Connecting ----------

    /// <summary>Swaps a short-lived user token for a long-lived one, then lists the Pages it manages with their own tokens
    /// (Page tokens made from a long-lived user token don't expire).</summary>
    public async Task<List<MetaPage>> PagesAsync(string appId, string appSecret, string userToken, CancellationToken ct = default)
    {
        var exchange = await GetAsync($"oauth/access_token?grant_type=fb_exchange_token&client_id={E(appId)}&client_secret={E(appSecret)}&fb_exchange_token={E(userToken)}", null, ct);
        var longLived = exchange.GetProperty("access_token").GetString()!;
        var pages = await GetAsync("me/accounts?fields=id,name,access_token,instagram_business_account{id,username}&limit=100", longLived, ct);
        return pages.GetProperty("data").EnumerateArray().Select(p =>
        {
            string? igId = null, igName = null;
            if (p.TryGetProperty("instagram_business_account", out var ig))
            {
                igId = ig.GetProperty("id").GetString();
                igName = ig.TryGetProperty("username", out var u) ? u.GetString() : null;
            }
            return new MetaPage(p.GetProperty("id").GetString()!, p.GetProperty("name").GetString()!, p.GetProperty("access_token").GetString()!, igId, igName);
        }).ToList();
    }

    // ---------- Instagram ----------

    /// <summary>Instagram's rolling 24-hour publishing usage and limit.</summary>
    public async Task<(int Used, int Limit)> InstagramQuotaAsync(string igId, string token, CancellationToken ct = default)
    {
        var r = await GetAsync($"{igId}/content_publishing_limit?fields=quota_usage,config", token, ct);
        var d = r.GetProperty("data")[0];
        var limit = d.TryGetProperty("config", out var c) && c.TryGetProperty("quota_total", out var t) ? t.GetInt32() : 100;
        return (d.TryGetProperty("quota_usage", out var u) ? u.GetInt32() : 0, limit);
    }

    /// <summary>One photo, or a carousel of 2-10. Photos must be public JPEG addresses.</summary>
    public async Task<PublishResult> PublishInstagramAsync(string igId, string token, IReadOnlyList<string> photoUrls, string caption, CancellationToken ct = default)
    {
        string container;
        if (photoUrls.Count == 1)
            container = await PostIdAsync($"{igId}/media", token, new() { ["image_url"] = photoUrls[0], ["caption"] = caption }, ct);
        else
        {
            var children = new List<string>();
            foreach (var url in photoUrls)
                children.Add(await PostIdAsync($"{igId}/media", token, new() { ["image_url"] = url, ["is_carousel_item"] = "true" }, ct));
            foreach (var child in children) await WaitReadyAsync(child, token, ct);
            container = await PostIdAsync($"{igId}/media", token,
                new() { ["media_type"] = "CAROUSEL", ["children"] = string.Join(",", children), ["caption"] = caption }, ct);
        }
        await WaitReadyAsync(container, token, ct);
        var id = await PostIdAsync($"{igId}/media_publish", token, new() { ["creation_id"] = container }, ct);
        string? link = null;
        try { link = (await GetAsync($"{id}?fields=permalink", token, ct)).GetProperty("permalink").GetString(); } catch (MetaException) { }
        return new PublishResult(id, link);
    }

    /// <summary>A reel (MP4 at a public address) with its cover image; shown in the feed as well as the Reels tab.</summary>
    public async Task<PublishResult> PublishInstagramReelAsync(string igId, string token, string videoUrl, string coverUrl, string caption, CancellationToken ct = default)
    {
        var container = await PostIdAsync($"{igId}/media", token, new()
        {
            ["media_type"] = "REELS", ["video_url"] = videoUrl, ["cover_url"] = coverUrl, ["caption"] = caption, ["share_to_feed"] = "true"
        }, ct);
        await WaitReadyAsync(container, token, ct, attempts: 120);   // videos take longer than photos to process
        var id = await PostIdAsync($"{igId}/media_publish", token, new() { ["creation_id"] = container }, ct);
        string? link = null;
        try { link = (await GetAsync($"{id}?fields=permalink", token, ct)).GetProperty("permalink").GetString(); } catch (MetaException) { }
        return new PublishResult(id, link);
    }

    private async Task WaitReadyAsync(string containerId, string token, CancellationToken ct, int attempts = 40)
    {
        for (var i = 0; i < attempts; i++)
        {
            var status = (await GetAsync($"{containerId}?fields=status_code", token, ct)).GetProperty("status_code").GetString();
            if (status is "FINISHED" or "PUBLISHED") return;
            if (status is "ERROR" or "EXPIRED") throw new MetaException($"Instagram could not process the photo ({status}).");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new MetaException("Instagram took too long to process the photos.");
    }

    // ---------- Facebook Page ----------

    public async Task<PublishResult> PublishFacebookAsync(string pageId, string token, IReadOnlyList<string> photoUrls, string message, CancellationToken ct = default)
    {
        var media = new List<string>();
        foreach (var url in photoUrls)
            media.Add(await PostIdAsync($"{pageId}/photos", token, new() { ["url"] = url, ["published"] = "false" }, ct));
        var form = new Dictionary<string, string> { ["message"] = message };
        for (var i = 0; i < media.Count; i++) form[$"attached_media[{i}]"] = JsonSerializer.Serialize(new { media_fbid = media[i] });
        var id = await PostIdAsync($"{pageId}/feed", token, form, ct);
        string? link = null;
        try { link = (await GetAsync($"{id}?fields=permalink_url", token, ct)).GetProperty("permalink_url").GetString(); } catch (MetaException) { }
        return new PublishResult(id, link);
    }

    /// <summary>A Facebook Page reel: start an upload, send the MP4 itself, then publish.
    /// Facebook allows 30 reels through the API per 24 hours.</summary>
    public async Task<PublishResult> PublishFacebookReelAsync(string pageId, string token, string videoFile, string description, CancellationToken ct = default)
    {
        var videoId = (await PostAsync($"{pageId}/video_reels", token, new() { ["upload_phase"] = "start" }, ct)).GetProperty("video_id").GetString()!;
        var bytes = await File.ReadAllBytesAsync(videoFile, ct);
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"https://rupload.facebook.com/video-upload/{Version}/{videoId}") { Content = new ByteArrayContent(bytes) })
        {
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
            req.Headers.TryAddWithoutValidation("offset", "0");
            req.Headers.TryAddWithoutValidation("file_size", bytes.Length.ToString());
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var res = await Client.SendAsync(req, ct);
            await ReadAsync(res, ct);
        }
        await PostAsync($"{pageId}/video_reels", token, new()
        {
            ["video_id"] = videoId, ["upload_phase"] = "finish", ["video_state"] = "PUBLISHED", ["description"] = description
        }, ct);
        // Wait for Facebook to process it (a failure shows up here); still processing after a few minutes = it will finish on its own
        for (var i = 0; i < 60; i++)
        {
            var status = (await GetAsync($"{videoId}?fields=status", token, ct)).GetProperty("status");
            var state = status.TryGetProperty("video_status", out var v) ? v.GetString() : null;
            if (state is "ready") break;
            if (state is "error" or "upload_failed" or "expired")
                throw new MetaException($"Facebook could not process the reel ({state}).");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return new PublishResult(videoId, $"https://www.facebook.com/reel/{videoId}");
    }

    /// <summary>Changes the text of a Page post made by this app (photos can't be changed).</summary>
    public Task UpdateFacebookAsync(string postId, string token, string message, CancellationToken ct = default) =>
        PostAsync(postId, token, new() { ["message"] = message }, ct);

    /// <summary>Deletes an Instagram post or a Facebook Page post.</summary>
    public async Task DeleteAsync(string id, string token, CancellationToken ct = default)
    {
        using var res = await Client.DeleteAsync($"{Base}{id}?access_token={E(token)}", ct);
        await ReadAsync(res, ct);
    }

    // ---------- Performance ----------

    /// <summary>Instagram post or reel numbers. Views, reach, saves and shares need instagram_manage_insights; likes and
    /// comments come from the post itself when that permission is missing (<see cref="PostStats.NeedsPermission"/>).</summary>
    public async Task<PostStats> InstagramStatsAsync(string mediaId, string token, CancellationToken ct = default)
    {
        try
        {
            var m = Metrics(await GetAsync($"{mediaId}/insights?metric=views,reach,likes,comments,saved,shares", token, ct));
            return new PostStats(m.Num("views"), m.Num("reach"), m.Num("likes"), m.Num("comments"), m.Num("shares"), m.Num("saved"), false);
        }
        catch (MetaException ex) when (ex.IsPermission || ex.Code == 100)
        {
            var r = await GetAsync($"{mediaId}?fields=like_count,comments_count", token, ct);
            return new PostStats(null, null, Int(r, "like_count"), Int(r, "comments_count"), null, null, ex.IsPermission);
        }
    }

    /// <summary>Facebook Page post (or reel, by its video id) numbers: reactions, comments and shares from the post; views
    /// and reach from its insights (read_insights).</summary>
    public async Task<PostStats> FacebookStatsAsync(string id, bool reel, string token, CancellationToken ct = default)
    {
        var r = await GetAsync(reel
            ? $"{id}?fields=likes.summary(true).limit(0),comments.summary(true).limit(0)"
            : $"{id}?fields=reactions.summary(total_count).limit(0),comments.summary(total_count).limit(0),shares", token, ct);
        int? Total(string field) => r.TryGetProperty(field, out var f) && f.TryGetProperty("summary", out var s) ? Int(s, "total_count") : null;
        var likes = Total(reel ? "likes" : "reactions");
        var comments = Total("comments");
        int? shares = r.TryGetProperty("shares", out var sh) ? Int(sh, "count") : reel ? null : 0;
        int? views = null, reach = null;
        var missing = false;
        try
        {
            if (reel)
            {
                var m = Metrics(await GetAsync($"{id}/video_insights?metric=blue_reels_play_count,post_impressions_unique", token, ct));
                views = m.Num("blue_reels_play_count"); reach = m.Num("post_impressions_unique");
            }
            else
            {
                // Meta replaced "impressions" with "media views" in November 2025
                var m = Metrics(await GetAsync($"{id}/insights?metric=post_media_view,post_total_media_view_unique", token, ct));
                views = m.Num("post_media_view"); reach = m.Num("post_total_media_view_unique");
            }
        }
        catch (MetaException ex) when (ex.IsPermission || ex.Code == 100) { missing = ex.IsPermission; }
        return new PostStats(views, reach, likes, comments, shares, null, missing);
    }

    // ---------- Comments ----------

    /// <summary>Comments on an Instagram post with their replies (instagram_manage_comments).</summary>
    public async Task<List<FetchedComment>> InstagramCommentsAsync(string mediaId, string token, CancellationToken ct = default)
    {
        var list = new List<FetchedComment>();
        var r = await GetAsync($"{mediaId}/comments?fields=id,text,username,timestamp,replies{{id,text,username,timestamp}}&limit=50", token, ct);
        foreach (var c in r.GetProperty("data").EnumerateArray())
        {
            var top = Comment(c, null, "text", "username");
            list.Add(top);
            if (c.TryGetProperty("replies", out var rep) && rep.TryGetProperty("data", out var rd))
                list.AddRange(rd.EnumerateArray().Select(x => Comment(x, top.Id, "text", "username")));
        }
        return list;
    }

    /// <summary>Comments on a Facebook Page post or reel with their replies. The commenter's name needs pages_read_user_content.</summary>
    public async Task<List<FetchedComment>> FacebookCommentsAsync(string id, string token, CancellationToken ct = default)
    {
        var list = new List<FetchedComment>();
        var r = await GetAsync($"{id}/comments?fields=id,message,created_time,from{{id,name}},comments.limit(50){{id,message,created_time,from{{id,name}}}}&filter=toplevel&limit=50", token, ct);
        foreach (var c in r.GetProperty("data").EnumerateArray())
        {
            var top = Comment(c, null, "message", null);
            list.Add(top);
            if (c.TryGetProperty("comments", out var rep) && rep.TryGetProperty("data", out var rd))
                list.AddRange(rd.EnumerateArray().Select(x => Comment(x, top.Id, "message", null)));
        }
        return list;
    }

    /// <summary>Replies to an Instagram comment (instagram_manage_comments).</summary>
    public Task<string> ReplyInstagramAsync(string commentId, string token, string message, CancellationToken ct = default) =>
        PostIdAsync($"{commentId}/replies", token, new() { ["message"] = message }, ct);

    /// <summary>Replies to a Facebook comment as the Page (pages_manage_engagement).</summary>
    public Task<string> ReplyFacebookAsync(string commentId, string token, string message, CancellationToken ct = default) =>
        PostIdAsync($"{commentId}/comments", token, new() { ["message"] = message }, ct);

    private static FetchedComment Comment(JsonElement c, string? parent, string textField, string? userField)
    {
        string author; string? authorId = null;
        if (userField is not null) author = Str(c, userField) ?? "Instagram user";
        else if (c.TryGetProperty("from", out var f)) { author = Str(f, "name") ?? "Facebook user"; authorId = Str(f, "id"); }
        else author = "Facebook user";
        return new FetchedComment(Str(c, "id")!, parent, author, authorId, Str(c, textField) ?? "",
            ParseTime(Str(c, "timestamp") ?? Str(c, "created_time")), null);
    }

    // ---------- Reading answers ----------

    /// <summary>Insights answers: {"data":[{"name":"views","values":[{"value":12}]}]} (or "total_value") → name → number.</summary>
    internal static Dictionary<string, long> Metrics(JsonElement r)
    {
        var d = new Dictionary<string, long>();
        if (!r.TryGetProperty("data", out var data)) return d;
        foreach (var m in data.EnumerateArray())
        {
            var name = Str(m, "name");
            if (name is null) continue;
            JsonElement v = default;
            if (m.TryGetProperty("values", out var vals) && vals.GetArrayLength() > 0) vals[0].TryGetProperty("value", out v);
            else if (m.TryGetProperty("total_value", out var tv)) tv.TryGetProperty("value", out v);
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) d[name] = n;
        }
        return d;
    }

    internal static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Meta's times look like 2026-10-28T10:15:00+0000.</summary>
    internal static DateTime ParseTime(string? s)
    {
        if (s is null) return DateTime.UtcNow;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"[+-]\d{4}$")) s = s[..^2] + ":" + s[^2..];
        return DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime : DateTime.UtcNow;
    }

    // ---------- HTTP ----------

    private async Task<string> PostIdAsync(string path, string token, Dictionary<string, string> form, CancellationToken ct) =>
        (await PostAsync(path, token, form, ct)).GetProperty("id").GetString()!;

    private async Task<JsonElement> PostAsync(string path, string token, Dictionary<string, string> form, CancellationToken ct)
    {
        form["access_token"] = token;
        using var res = await Client.PostAsync(Base + path, new FormUrlEncodedContent(form), ct);
        return await ReadAsync(res, ct);
    }

    private async Task<JsonElement> GetAsync(string pathAndQuery, string? token, CancellationToken ct)
    {
        var url = Base + pathAndQuery + (token is null ? "" : (pathAndQuery.Contains('?') ? "&" : "?") + "access_token=" + E(token));
        using var res = await Client.GetAsync(url, ct);
        return await ReadAsync(res, ct);
    }

    internal static async Task<JsonElement> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        string Snip() => text.Length > 300 ? text[..300] : text;
        JsonElement body;
        try { body = JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException) { throw new MetaException($"Meta answered {(int)res.StatusCode}: {Snip()}"); }
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("error_user_msg", out var um) ? um.GetString() : err.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new MetaException(msg ?? "Meta reported an error.", err.TryGetProperty("code", out var c) ? c.GetInt32() : null);
        }
        // The video upload host answers {"debug_info": {"message": …}} instead
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("debug_info", out var dbg) && dbg.TryGetProperty("message", out var dm))
            throw new MetaException($"Meta answered {(int)res.StatusCode}: {dm.GetString()}");
        if (!res.IsSuccessStatusCode) throw new MetaException($"Meta answered {(int)res.StatusCode}: {Snip()}");
        return body;
    }

    private static string E(string s) => Uri.EscapeDataString(s);
}

internal static class MetricsExtensions
{
    public static int? Num(this Dictionary<string, long> d, string name) => d.TryGetValue(name, out var v) ? (int)Math.Min(v, int.MaxValue) : null;
}
