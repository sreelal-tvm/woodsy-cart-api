using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace WoodsyCart.Infrastructure.Social;

public record ThreadsToken(string Token, DateTime ExpiresAt);

/// <summary>
/// The Threads API calls WoodsyCart needs. Threads has its own login (graph.threads.com, its own app ID and secret inside
/// the same Meta app): a short code becomes a 60-day token, which is refreshed before it runs out.
/// </summary>
public class ThreadsClient(IHttpClientFactory http, IConfiguration config)
{
    public const string Scopes = "threads_basic,threads_content_publish,threads_delete,threads_manage_insights,threads_read_replies,threads_manage_replies";
    private const string Host = "https://graph.threads.com/";
    private string Base => Host + (config["Social:ThreadsVersion"] ?? "v1.0") + "/";
    private HttpClient Client => http.CreateClient("meta");

    // ---------- Connecting ----------

    public static string AuthorizeUrl(string appId, string redirectUri, string state) =>
        $"https://threads.com/oauth/authorize?client_id={E(appId)}&redirect_uri={E(redirectUri)}&scope={E(Scopes)}&response_type=code&state={E(state)}";

    /// <summary>Code from the login redirect → short-lived token → 60-day token, plus the account it belongs to.</summary>
    public async Task<(ThreadsToken Token, string UserId, string Username)> ConnectAsync(string appId, string appSecret, string redirectUri, string code, CancellationToken ct = default)
    {
        using var res = await Client.PostAsync(Host + "oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = appId, ["client_secret"] = appSecret, ["grant_type"] = "authorization_code", ["redirect_uri"] = redirectUri, ["code"] = code
        }), ct);
        var shortLived = (await MetaClient.ReadAsync(res, ct)).GetProperty("access_token").GetString()!;
        var token = Expiring(await GetAsync($"{Host}access_token?grant_type=th_exchange_token&client_secret={E(appSecret)}&access_token={E(shortLived)}", ct));
        var me = await GetAsync($"{Base}me?fields=id,username&access_token={E(token.Token)}", ct);
        return (token, me.GetProperty("id").GetString()!, me.GetProperty("username").GetString()!);
    }

    /// <summary>A fresh 60-day token (the current one must be at least a day old and not expired).</summary>
    public async Task<ThreadsToken> RefreshAsync(string token, CancellationToken ct = default) =>
        Expiring(await GetAsync($"{Host}refresh_access_token?grant_type=th_refresh_token&access_token={E(token)}", ct));

    // ---------- Posting ----------

    /// <summary>Threads' rolling 24-hour publishing usage and limit (250).</summary>
    public async Task<(int Used, int Limit)> QuotaAsync(string userId, string token, CancellationToken ct = default)
    {
        var d = (await GetAsync($"{Base}{userId}/threads_publishing_limit?fields=quota_usage,config&access_token={E(token)}", ct)).GetProperty("data")[0];
        var limit = d.TryGetProperty("config", out var c) && c.TryGetProperty("quota_total", out var t) ? t.GetInt32() : 250;
        return (d.TryGetProperty("quota_usage", out var u) ? u.GetInt32() : 0, limit);
    }

    /// <summary>One photo, or a carousel of 2-20, with the text and one topic tag. Photos must be public addresses.</summary>
    public async Task<PublishResult> PublishAsync(string userId, string token, IReadOnlyList<string> photoUrls, string text, string? topic, CancellationToken ct = default)
    {
        var post = new Dictionary<string, string> { ["text"] = text };
        if (!string.IsNullOrWhiteSpace(topic)) post["topic_tag"] = topic.Trim();
        string container;
        if (photoUrls.Count == 1)
        {
            post["media_type"] = "IMAGE"; post["image_url"] = photoUrls[0];
            container = await PostIdAsync($"{userId}/threads", token, post, ct);
        }
        else
        {
            var children = new List<string>();
            foreach (var url in photoUrls)
                children.Add(await PostIdAsync($"{userId}/threads", token, new() { ["media_type"] = "IMAGE", ["image_url"] = url, ["is_carousel_item"] = "true" }, ct));
            foreach (var child in children) await WaitReadyAsync(child, token, ct);
            post["media_type"] = "CAROUSEL"; post["children"] = string.Join(",", children);
            container = await PostIdAsync($"{userId}/threads", token, post, ct);
        }
        await WaitReadyAsync(container, token, ct);
        var id = await PostIdAsync($"{userId}/threads_publish", token, new() { ["creation_id"] = container }, ct);
        string? link = null;
        try { link = (await GetAsync($"{Base}{id}?fields=permalink&access_token={E(token)}", ct)).GetProperty("permalink").GetString(); } catch (MetaException) { }
        return new PublishResult(id, link);
    }

    /// <summary>A video post (the product reel, MP4 at a public address) with the text and topic tag.</summary>
    public async Task<PublishResult> PublishVideoAsync(string userId, string token, string videoUrl, string text, string? topic, CancellationToken ct = default)
    {
        var post = new Dictionary<string, string> { ["media_type"] = "VIDEO", ["video_url"] = videoUrl, ["text"] = text };
        if (!string.IsNullOrWhiteSpace(topic)) post["topic_tag"] = topic.Trim();
        var container = await PostIdAsync($"{userId}/threads", token, post, ct);
        await WaitReadyAsync(container, token, ct, attempts: 120);   // videos take longer than photos
        var id = await PostIdAsync($"{userId}/threads_publish", token, new() { ["creation_id"] = container }, ct);
        string? link = null;
        try { link = (await GetAsync($"{Base}{id}?fields=permalink&access_token={E(token)}", ct)).GetProperty("permalink").GetString(); } catch (MetaException) { }
        return new PublishResult(id, link);
    }

    public async Task DeleteAsync(string id, string token, CancellationToken ct = default)
    {
        using var res = await Client.DeleteAsync($"{Base}{id}?access_token={E(token)}", ct);
        await MetaClient.ReadAsync(res, ct);
    }

    // ---------- Performance and replies ----------

    /// <summary>A post's views, likes, replies and shares (reposts + quotes + shares); needs threads_manage_insights.</summary>
    public async Task<PostStats> StatsAsync(string mediaId, string token, CancellationToken ct = default)
    {
        try
        {
            Dictionary<string, long> m;
            try { m = MetaClient.Metrics(await GetAsync($"{Base}{mediaId}/insights?metric=views,likes,replies,reposts,quotes,shares&access_token={E(token)}", ct)); }
            // "views" and "shares" are newer metrics: without them if Threads refuses the list
            catch (MetaException ex) when (ex.Code == 100) { m = MetaClient.Metrics(await GetAsync($"{Base}{mediaId}/insights?metric=likes,replies,reposts,quotes&access_token={E(token)}", ct)); }
            int? shares = m.ContainsKey("reposts") || m.ContainsKey("quotes") || m.ContainsKey("shares")
                ? (m.Num("reposts") ?? 0) + (m.Num("quotes") ?? 0) + (m.Num("shares") ?? 0) : null;
            return new PostStats(m.Num("views"), null, m.Num("likes"), m.Num("replies"), shares, null, false);
        }
        catch (MetaException ex) when (ex.IsPermission) { return new PostStats(null, null, null, null, null, null, true); }
    }

    /// <summary>Every reply under a post, nested ones too (threads_read_replies). ParentId = the post or reply it answers.</summary>
    public async Task<List<FetchedComment>> RepliesAsync(string mediaId, string token, CancellationToken ct = default)
    {
        var r = await GetAsync($"{Base}{mediaId}/conversation?fields=id,text,username,timestamp,replied_to,is_reply_owned_by_me&reverse=false&access_token={E(token)}", ct);
        return r.GetProperty("data").EnumerateArray().Select(c =>
        {
            var parent = c.TryGetProperty("replied_to", out var rt) ? MetaClient.Str(rt, "id") : null;
            var mine = c.TryGetProperty("is_reply_owned_by_me", out var o) && o.ValueKind == JsonValueKind.True;
            return new FetchedComment(MetaClient.Str(c, "id")!, parent == mediaId ? null : parent, MetaClient.Str(c, "username") ?? "Threads user", null,
                MetaClient.Str(c, "text") ?? "", MetaClient.ParseTime(MetaClient.Str(c, "timestamp")), mine);
        }).ToList();
    }

    /// <summary>Replies to a Threads reply (threads_manage_replies). Returns the new reply's id.</summary>
    public async Task<string> ReplyAsync(string userId, string token, string replyToId, string text, CancellationToken ct = default)
    {
        var container = await PostIdAsync($"{userId}/threads", token, new() { ["media_type"] = "TEXT", ["text"] = text, ["reply_to_id"] = replyToId }, ct);
        await WaitReadyAsync(container, token, ct);
        return await PostIdAsync($"{userId}/threads_publish", token, new() { ["creation_id"] = container }, ct);
    }

    private async Task WaitReadyAsync(string containerId, string token, CancellationToken ct, int attempts = 40)
    {
        for (var i = 0; i < attempts; i++)
        {
            var r = await GetAsync($"{Base}{containerId}?fields=status,error_message&access_token={E(token)}", ct);
            var status = r.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status is "FINISHED" or "PUBLISHED") return;
            if (status is "ERROR" or "EXPIRED")
                throw new MetaException($"Threads could not process the photo ({(r.TryGetProperty("error_message", out var m) ? m.GetString() : status)}).");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        throw new MetaException("Threads took too long to process the photos.");
    }

    // ---------- HTTP ----------

    private async Task<string> PostIdAsync(string path, string token, Dictionary<string, string> form, CancellationToken ct)
    {
        form["access_token"] = token;
        using var res = await Client.PostAsync(Base + path, new FormUrlEncodedContent(form), ct);
        return (await MetaClient.ReadAsync(res, ct)).GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetAsync(string url, CancellationToken ct)
    {
        using var res = await Client.GetAsync(url, ct);
        return await MetaClient.ReadAsync(res, ct);
    }

    private static ThreadsToken Expiring(JsonElement r) => new(r.GetProperty("access_token").GetString()!,
        DateTime.UtcNow.AddSeconds(r.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? s : 60L * 24 * 3600));

    private static string E(string s) => Uri.EscapeDataString(s);
}
