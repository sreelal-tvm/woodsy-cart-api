using Microsoft.AspNetCore.Mvc;
using WoodsyCart.Infrastructure.Social;

namespace WoodsyCart.Api.Controllers;

/// <summary>Threads login, step 2: Threads sends the admin's browser here with a one-time code (no WoodsyCart login on this
/// request; the random state from step 1 proves it). Ends back in Admin → Social media → Settings.</summary>
[Route("api/social/threads")]
public class SocialCallbackController(SocialService social) : ApiControllerBase
{
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? code, string? state, string? error) =>
        Redirect(await social.ThreadsCallbackAsync(code, state, error));
}

/// <summary>YouTube (Google) login, step 2: the same, for the YouTube channel.</summary>
[Route("api/social/youtube")]
public class YouTubeCallbackController(SocialService social) : ApiControllerBase
{
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? code, string? state, string? error) =>
        Redirect(await social.YouTubeCallbackAsync(code, state, error));
}
