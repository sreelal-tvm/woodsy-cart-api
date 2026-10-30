using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WoodsyCart.Infrastructure.Push;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

public record PushRemoveRequest(string? Endpoint, int? Id);

/// <summary>Admin → Email &amp; notifications → Phone notifications: turn new-order notifications on or off per device.</summary>
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

    [HttpPost("test")]
    public async Task<object> Test() => new { devices = await push.TestAsync(UserId, urls.Value.AdminUrl ?? "") };
}
