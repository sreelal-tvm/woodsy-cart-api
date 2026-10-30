using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WoodsyCart.Infrastructure.Push;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

public record PushRemoveRequest(string? Endpoint, int? Id);

/// <summary>Admin → Email &amp; notifications → App notifications: devices and which notifications go out.</summary>
[Route("api/admin/push")]
public class AdminPushController(PushService push, IOptions<SmtpOptions> urls) : AdminControllerBase
{
    /// <summary>The server key the browser needs, whether this device is on, and this person's devices.</summary>
    [HttpGet("status")]
    public Task<PushStatusDto> Status([FromQuery] string? endpoint) => push.StatusAsync(UserId, endpoint);

    [HttpPost("subscribe")]
    public async Task<PushStatusDto> Subscribe(PushSubscribeRequest r)
    {
        await push.SubscribeAsync(UserId, r, Request.Headers.UserAgent.ToString());
        return await push.StatusAsync(UserId, r.Endpoint);
    }

    [HttpPost("remove")]
    public async Task<IActionResult> Remove(PushRemoveRequest r) { await push.RemoveAsync(UserId, r.Endpoint, r.Id); return NoContent(); }

    /// <summary>Which app notifications go out: to customers, to the team, and per event (Admin → App notifications).</summary>
    [HttpGet("settings")]
    public Task<PushSettingsDto> Settings() => push.SettingsAsync();

    [HttpPut("settings")]
    public async Task<PushSettingsDto> SaveSettings(PushSettingsUpdate r) { await push.SaveSettingsAsync(r); return await push.SettingsAsync(); }

    /// <summary>Notifications written in Admin, and offers / new arrivals sent automatically (newest first).</summary>
    [HttpGet("campaigns")]
    public Task<List<PushCampaignDto>> Campaigns() => push.CampaignsAsync();

    /// <summary>How many customer devices each audience reaches (only those that take offers and news).</summary>
    [HttpGet("campaigns/audience")]
    public Task<PushAudienceDto> Audience() => push.AudienceAsync();

    [HttpPost("campaigns/test")]
    public async Task<object> TestCampaign(PushCampaignRequest r) => new { devices = await push.TestCampaignAsync(UserId, r) };

    [HttpPost("campaigns")]
    public Task<PushCampaignDto> SendCampaign(PushCampaignRequest r) => push.SendCampaignAsync(r, UserName);

    [HttpPost("test")]
    public async Task<object> Test() => new { devices = await push.TestAsync(UserId, urls.Value.AdminUrl ?? "") };
}
