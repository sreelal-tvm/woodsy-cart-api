using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace WoodsyCart.Infrastructure.Social;

public class MetaException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
    /// <summary>The token was revoked or expired (Meta error 190): reconnect in Admin.</summary>
    public bool TokenInvalid => Code == 190;
}

public record MetaPage(string Id, string Name, string Token, string? InstagramId, string? InstagramUsername);
public record PublishResult(string Id, string? Permalink);

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

    /// <summary>A Facebook Page reel: start an upload, let Facebook fetch the MP4 from its public address, then publish.
    /// Facebook allows 30 reels through the API per 24 hours.</summary>
    public async Task<PublishResult> PublishFacebookReelAsync(string pageId, string token, string videoUrl, string description, CancellationToken ct = default)
    {
        var videoId = (await PostAsync($"{pageId}/video_reels", token, new() { ["upload_phase"] = "start" }, ct)).GetProperty("video_id").GetString()!;
        using (var req = new HttpRequestMessage(HttpMethod.Post, $"https://rupload.facebook.com/video-upload/{Version}/{videoId}"))
        {
            req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
            req.Headers.TryAddWithoutValidation("file_url", videoUrl);
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
        JsonElement body;
        try { body = await res.Content.ReadFromJsonAsync<JsonElement>(ct); }
        catch (JsonException) { throw new MetaException($"Meta answered {(int)res.StatusCode} without details."); }
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var err))
        {
            var msg = err.TryGetProperty("error_user_msg", out var um) ? um.GetString() : err.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new MetaException(msg ?? "Meta reported an error.", err.TryGetProperty("code", out var c) ? c.GetInt32() : null);
        }
        if (!res.IsSuccessStatusCode) throw new MetaException($"Meta answered {(int)res.StatusCode}.");
        return body;
    }

    private static string E(string s) => Uri.EscapeDataString(s);
}
