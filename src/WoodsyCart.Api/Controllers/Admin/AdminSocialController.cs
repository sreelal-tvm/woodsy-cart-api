using Microsoft.AspNetCore.Mvc;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Social;

namespace WoodsyCart.Api.Controllers.Admin;

public record SocialCaptionRequest(string? Caption);

/// <summary>Admin → Social media: Instagram and Facebook posting of products (one post per product per platform).</summary>
[Route("api/admin/social")]
public class AdminSocialController(SocialService social) : AdminControllerBase
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

    /// <summary>Queue and posted lists.</summary>
    [HttpGet("posts")]
    public Task<PagedResult<SocialPostRow>> Posts(SocialPostStatus? status, SocialPlatform? platform, int page = 1, int pageSize = 25) =>
        social.ListAsync(status, platform, page, pageSize);

    /// <summary>A product's caption preview and its post on each platform.</summary>
    [HttpGet("products/{id:int}")]
    public Task<SocialProductDto> Product(int id) => social.ProductAsync(id);

    /// <summary>Post products now (up to today's limit; the rest scheduled) or add them to the schedule.</summary>
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
