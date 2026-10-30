using Microsoft.AspNetCore.Mvc;
using WoodsyCart.Infrastructure.Push;

namespace WoodsyCart.Api.Controllers;

public record PushEndpointRequest(string Endpoint);

/// <summary>Storefront: a customer turns app notifications on or off for this phone (the app's prompt, or the order page).</summary>
[Route("api/push")]
public class PushController(PushService push) : ApiControllerBase
{
    /// <summary>The server key the browser needs, whether this device is on, and whether the shop sends customer notifications.</summary>
    [HttpGet("status")]
    public Task<CustomerPushStatusDto> Status([FromQuery] string? endpoint) => push.CustomerStatusAsync(endpoint);

    [HttpPost("subscribe")]
    public async Task<CustomerPushStatusDto> Subscribe(CustomerPushRequest r)
    {
        await push.SubscribeCustomerAsync(OptionalUserId, r, Request.Headers.UserAgent.ToString());
        return await push.CustomerStatusAsync(r.Endpoint);
    }

    /// <summary>Offers, new arrivals and reminders on this device too, or order updates only.</summary>
    [HttpPost("preferences")]
    public async Task<IActionResult> Preferences(PushPreferencesRequest r) { await push.SetPreferencesAsync(r); return NoContent(); }

    /// <summary>The cart on this device changed (for one "your cart is waiting" reminder).</summary>
    [HttpPost("cart")]
    public async Task<IActionResult> Cart(PushCartRequest r) { await push.CartAsync(r); return NoContent(); }

    [HttpPost("unsubscribe")]
    public async Task<IActionResult> Unsubscribe(PushEndpointRequest r) { await push.RemoveCustomerAsync(r.Endpoint); return NoContent(); }
}
