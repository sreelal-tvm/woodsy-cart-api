using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace WoodsyCart.Infrastructure.Social;

public record YouTubeChannel(string Id, string Title, string? Handle);
public record YouTubeVideo(string Title, string Description, List<string> Tags);

/// <summary>
/// The YouTube Data API calls WoodsyCart needs: Google login for the channel (the code becomes a refresh token, which gets an
/// access token for an hour at a time), upload a Short, change its title and description, delete it. Errors come back as
/// <see cref="MetaException"/> like the other platforms' (190 = connect again, 10 = missing permission, 100 = not found).
/// </summary>
public class YouTubeClient(IHttpClientFactory http)
{
    /// <summary>Upload, edit and delete videos and read the channel's name: this one scope covers all of it.</summary>
    public const string Scope = "https://www.googleapis.com/auth/youtube.force-ssl";
    private const string TokenUrl = "https://oauth2.googleapis.com/token";
    private const string Api = "https://www.googleapis.com/youtube/v3/";
    public const string CategoryId = "26";   // Howto & Style
    private HttpClient Client => http.CreateClient("youtube");

    // ---------- Connecting ----------

    /// <summary>Google's login page; "select_account" lets the admin pick the Google account that owns the channel, "consent"
    /// makes Google give a refresh token every time.</summary>
    public static string AuthorizeUrl(string clientId, string redirectUri, string state) =>
        $"https://accounts.google.com/o/oauth2/v2/auth?client_id={E(clientId)}&redirect_uri={E(redirectUri)}&response_type=code&scope={E(Scope)}"
        + $"&access_type=offline&prompt={E("select_account consent")}&state={E(state)}";

    /// <summary>Code from the login redirect → refresh token, plus the channel it belongs to.</summary>
    public async Task<(string RefreshToken, YouTubeChannel Channel)> ConnectAsync(string clientId, string clientSecret, string redirectUri, string code, CancellationToken ct = default)
    {
        using var res = await Client.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["grant_type"] = "authorization_code", ["redirect_uri"] = redirectUri, ["code"] = code
        }), ct);
        var r = await ReadAsync(res, ct);
        var refresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
            throw new MetaException("Google didn't give lasting access. Remove WoodsyCart under myaccount.google.com → Security → Third-party connections, then connect again.");
        return (refresh, await ChannelAsync(r.GetProperty("access_token").GetString()!, ct));
    }

    /// <summary>The channel of the signed-in Google account.</summary>
    public async Task<YouTubeChannel> ChannelAsync(string accessToken, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Get, Api + "channels?part=snippet&mine=true", accessToken, null, ct);
        if (!r.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            throw new MetaException("This Google account has no YouTube channel. Log in with the account that owns the WoodsyCart channel.");
        var ch = items[0];
        var snippet = ch.GetProperty("snippet");
        return new YouTubeChannel(ch.GetProperty("id").GetString()!, MetaClient.Str(snippet, "title") ?? "YouTube channel", MetaClient.Str(snippet, "customUrl"));
    }

    /// <summary>An access token for the next hour.</summary>
    public async Task<(string Token, DateTime ExpiresAt)> AccessTokenAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct = default)
    {
        using var res = await Client.PostAsync(TokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = clientSecret, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken
        }), ct);
        var r = await ReadAsync(res, ct);
        var seconds = r.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
        return (r.GetProperty("access_token").GetString()!, DateTime.UtcNow.AddSeconds(seconds - 120));
    }

    // ---------- Posting ----------

    /// <summary>Uploads the reel (MP4 on this server) as a Short: vertical and under 3 minutes, so YouTube shows it as one.</summary>
    public async Task<PublishResult> UploadAsync(string accessToken, string videoPath, YouTubeVideo v, string privacy, CancellationToken ct = default)
    {
        var length = new FileInfo(videoPath).Length;
        using var start = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status")
        {
            Content = JsonContent.Create(new
            {
                snippet = Snippet(v),
                status = new { privacyStatus = privacy, selfDeclaredMadeForKids = false, embeddable = true, license = "youtube", containsSyntheticMedia = false }
            })
        };
        start.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        start.Headers.Add("X-Upload-Content-Type", "video/mp4");
        start.Headers.Add("X-Upload-Content-Length", length.ToString());
        Uri location;
        using (var res = await Client.SendAsync(start, ct))
        {
            await ReadAsync(res, ct);
            location = res.Headers.Location ?? throw new MetaException("YouTube didn't accept the upload (no upload address).");
        }
        await using var file = File.OpenRead(videoPath);
        using var put = new HttpRequestMessage(HttpMethod.Put, location) { Content = new StreamContent(file) };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        put.Content.Headers.ContentLength = length;
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var done = await Client.SendAsync(put, ct);
        var id = (await ReadAsync(done, ct)).GetProperty("id").GetString()!;
        return new PublishResult(id, $"https://www.youtube.com/shorts/{id}");
    }

    /// <summary>New title, description and tags for a live video (the video itself stays).</summary>
    public Task UpdateAsync(string accessToken, string videoId, YouTubeVideo v, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, Api + "videos?part=snippet", accessToken, new { id = videoId, snippet = Snippet(v) }, ct);

    public Task DeleteAsync(string accessToken, string videoId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, Api + "videos?id=" + E(videoId), accessToken, null, ct);

    private static object Snippet(YouTubeVideo v) => new
    {
        title = v.Title, description = v.Description, tags = v.Tags, categoryId = CategoryId, defaultLanguage = "en", defaultAudioLanguage = "en"
    };

    // ---------- HTTP ----------

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, string accessToken, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await Client.SendAsync(req, ct);
        return await ReadAsync(res, ct);
    }

    /// <summary>The response's JSON (empty for 204), or a <see cref="MetaException"/> with Google's reason.</summary>
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonElement json = default;
        if (text.Length > 0) try { json = JsonDocument.Parse(text).RootElement.Clone(); } catch (JsonException) { }
        if (res.IsSuccessStatusCode) return json;

        string? message = null, reason = null;
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error", out var err))
        {
            if (err.ValueKind == JsonValueKind.String)
            {
                // Login errors: {"error": "invalid_grant", "error_description": "…"}
                reason = err.GetString();
                message = MetaClient.Str(json, "error_description") ?? reason;
            }
            else
            {
                message = MetaClient.Str(err, "message");
                if (err.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0)
                    reason = MetaClient.Str(list[0], "reason");
            }
        }
        message ??= $"YouTube answered {(int)res.StatusCode} {res.ReasonPhrase}.";
        int? code = reason switch
        {
            "invalid_grant" or "authError" => 190,
            "insufficientPermissions" or "forbidden" => 10,
            "videoNotFound" => 100,
            _ => res.StatusCode switch { HttpStatusCode.Unauthorized => 190, HttpStatusCode.NotFound => 100, _ => null }
        };
        if (reason is "quotaExceeded" or "uploadLimitExceeded" or "rateLimitExceeded")
            message = "YouTube's daily limit for this channel or app is used up. " + message;
        throw new MetaException(message, code);
    }

    private static string E(string s) => Uri.EscapeDataString(s);
}
