using Microsoft.AspNetCore.Mvc;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Social;

namespace WoodsyCart.Api.Controllers.Admin;

public record SocialCaptionRequest(string? Caption);
public record FeedSwitch(bool Enabled);

/// <summary>Admin → Social media: Instagram, Facebook and Threads posting of products (one post per product per platform).</summary>
[Route("api/admin/social")]
public class AdminSocialController(SocialService social, CatalogFeed feed, SocialEngagement engagement) : AdminControllerBase
{
    [HttpGet("summary")]
    public Task<object> Summary() => social.SummaryAsync();

    [HttpGet("settings")]
    public Task<SocialSettingsDto> Settings() => social.SettingsAsync();

    [HttpPut("settings")]
    public async Task<SocialSettingsDto> SaveSettings(SocialSettingsUpdate r) { await social.SaveSettingsAsync(r); return await social.SettingsAsync(); }

    [HttpPost("connect")]
    public Task<SocialConnectResult> Connect(SocialConnectRequest r) => social.ConnectAsync(r);

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect() { await social.DisconnectAsync(); return NoContent(); }

    /// <summary>Threads login, step 1: the address to send the browser to (Threads comes back to /api/social/threads/callback).</summary>
    [HttpPost("threads/connect")]
    public object ConnectThreads(ThreadsConnectRequest r) => new { url = social.StartThreads(r) };

    [HttpPost("threads/disconnect")]
    public async Task<IActionResult> DisconnectThreads() { await social.DisconnectThreadsAsync(); return NoContent(); }

    /// <summary>YouTube login, step 1: the Google address to send the browser to (Google comes back to /api/social/youtube/callback).</summary>
    [HttpPost("youtube/connect")]
    public async Task<object> ConnectYouTube(YouTubeConnectRequest r) => new { url = await social.StartYouTubeAsync(r) };

    [HttpPost("youtube/disconnect")]
    public async Task<IActionResult> DisconnectYouTube() { await social.DisconnectYouTubeAsync(); return NoContent(); }

    /// <summary>Views, likes, comments and shares per platform for posts of the last <paramref name="days"/> days, and the best posts.</summary>
    [HttpGet("performance")]
    public Task<SocialPerformance> Performance(int days = 30) => engagement.PerformanceAsync(days);

    /// <summary>Comment conversations on our posts (unread = new comments since you last looked).</summary>
    [HttpGet("comments")]
    public Task<PagedResult<SocialCommentDto>> Comments(bool unread = false, string? account = null, int page = 1, int pageSize = 20, bool own = false, bool drafts = false) =>
        engagement.ListAsync(unread, account, page, pageSize, own, drafts);

    [HttpGet("comments/unread")]
    public async Task<object> UnreadComments() => new { count = await engagement.UnreadAsync() };

    [HttpGet("comments/issues")]
    public Task<List<string>> EngagementIssues() => engagement.IssuesAsync();

    [HttpPost("comments/{id:int}/reply")]
    public Task<SocialReplyDto> Reply(int id, SocialReplyRequest r) => engagement.ReplyAsync(id, r.Text);

    [HttpPost("comments/{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, bool read = true) { await engagement.MarkReadAsync(id, read); return NoContent(); }

    /// <summary>Drop the drafted answer of a conversation.</summary>
    [HttpPost("comments/{id:int}/dismiss")]
    public async Task<IActionResult> Dismiss(int id) { await engagement.DismissAsync(id); return NoContent(); }

    /// <summary>Draft the answer again (e.g. after changing the product's details).</summary>
    [HttpPost("comments/{id:int}/redraft")]
    public async Task<IActionResult> Redraft(int id) { await engagement.RedraftAsync(id); return NoContent(); }

    [HttpGet("auto-reply")]
    public Task<AutoReplySettingsDto> AutoReply() => engagement.AutoReplySettingsAsync();

    [HttpPut("auto-reply")]
    public Task<AutoReplySettingsDto> SaveAutoReply(AutoReplySettingsUpdate r) => engagement.SaveAutoReplySettingsAsync(r);

    /// <summary>What the assistant would answer to a comment on a product (nothing is posted).</summary>
    [HttpPost("auto-reply/test")]
    public Task<CommentSuggestion> TestAutoReply(AutoReplyTest t) => engagement.TestAsync(t);

    [HttpPost("comments/read-all")]
    public async Task<object> MarkAllRead() => new { count = await engagement.MarkAllReadAsync() };

    /// <summary>Queue and posted lists.</summary>
    [HttpGet("posts")]
    public Task<PagedResult<SocialPostRow>> Posts(SocialPostStatus? status, SocialPlatform? platform, int page = 1, int pageSize = 25) =>
        social.ListAsync(status, platform, page, pageSize);

    /// <summary>A product's caption preview and its post on each platform.</summary>
    [HttpGet("products/{id:int}")]
    public Task<SocialProductDto> Product(int id) => social.ProductAsync(id);

    /// <summary>Post products now (up to today's limit; the rest scheduled) or add them to the schedule.</summary>
    /// <summary>Makes the product's reel (about 20–60 seconds the first time) and returns its address.</summary>
    [HttpPost("products/{id:int}/reel")]
    public Task<ReelFile> MakeReel(int id, bool music = true, bool remake = false) => social.MakeReelAsync(id, music, remake);

    /// <summary>The product's story picture (1080×1920), made if needed.</summary>
    [HttpPost("products/{id:int}/story")]
    public async Task<object> MakeStory(int id) => new { image = await social.MakeStoryAsync(id) };

    [HttpGet("products/{id:int}/story/file")]
    public async Task<IActionResult> StoryFile(int id) =>
        PhysicalFile(social.FullPath(await social.MakeStoryAsync(id)), "image/jpeg", $"woodsycart-{id}-story.jpg");

    /// <summary>The reel as a download (made first if needed).</summary>
    [HttpGet("products/{id:int}/reel/file")]
    public async Task<IActionResult> ReelFile(int id, bool music = true)
    {
        var r = await social.MakeReelAsync(id, music);
        return PhysicalFile(social.ReelPath(r), "video/mp4", $"woodsycart-{id}-reel{(r.Track is null ? "" : "-music")}.mp4");
    }

    /// <summary>Product feed for Meta Commerce Manager: address, how many products are in it, when Meta last read it.</summary>
    [HttpGet("feed")]
    public Task<CatalogFeedStatus> Feed() => feed.StatusAsync();

    [HttpPost("feed")]
    public async Task<CatalogFeedStatus> SetFeed(FeedSwitch r)
    {
        var c = await social.ConfigAsync();
        c.FeedEnabled = r.Enabled;
        await social.SaveConfigAsync();
        if (r.Enabled) await feed.PrepareImagesAsync(10, HttpContext.RequestAborted);   // the first products straight away
        return await feed.StatusAsync();
    }

    [HttpGet("music")]
    public List<SocialMusicTrack> Music() => social.Music();

    [HttpPost("music")]
    [RequestSizeLimit(25_000_000)]
    public async Task<List<SocialMusicTrack>> AddMusic(IFormFile file)
    {
        if (file.Length > 20_000_000) throw new AppException("Music files can be at most 20 MB.");
        await using var stream = file.OpenReadStream();
        return await social.AddMusicAsync(stream, file.FileName);
    }

    [HttpDelete("music/{name}")]
    public List<SocialMusicTrack> DeleteMusic(string name) => social.DeleteMusic(name);

    [HttpPost("queue")]
    public Task<SocialQueueResult> Queue(SocialQueueRequest r)
    {
        if (r.ProductIds is not { Count: > 0 }) throw new AppException("Choose at least one product.");
        if (r.ProductIds.Count > 500) throw new AppException("Choose at most 500 products at a time.");
        return social.QueueAsync(r);
    }

    [HttpGet("posts/{id:int}/caption")]
    public async Task<object> CurrentCaption(int id) => new { caption = await social.CurrentCaptionAsync(id) };

    [HttpPut("posts/{id:int}/caption")]
    public async Task<IActionResult> SetCaption(int id, SocialCaptionRequest r) { await social.SetCaptionAsync(id, r.Caption); return NoContent(); }

    [HttpPost("posts/{id:int}/remove")]
    public async Task<IActionResult> Remove(int id) { await social.RemoveFromQueueAsync(id); return NoContent(); }

    [HttpPost("posts/{id:int}/delete")]
    public async Task<IActionResult> Delete(int id) { await social.DeletePostAsync(id); return NoContent(); }

    [HttpPost("posts/{id:int}/retry")]
    public async Task<IActionResult> Retry(int id) { await social.RetryAsync(id); return NoContent(); }

    [HttpPost("posts/{id:int}/mark-updated")]
    public async Task<IActionResult> MarkUpdated(int id) { await social.MarkUpdatedAsync(id); return NoContent(); }

    [HttpPost("posts/{id:int}/replace")]
    public async Task<IActionResult> Replace(int id) { await social.ReplaceAsync(id); return NoContent(); }

    /// <summary>Check live posts against their products now (normally every 10 minutes).</summary>
    [HttpPost("refresh")]
    public async Task<object> Refresh() => new { changed = await social.RefreshAsync() };
}
