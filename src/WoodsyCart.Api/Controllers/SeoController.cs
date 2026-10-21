using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using WoodsyCart.Api.Seo;

namespace WoodsyCart.Api.Controllers;

/// <summary>
/// robots.txt, sitemap.xml and link previews for the storefront (built by <see cref="SeoBuilder"/>).
/// nginx passes /robots.txt, /sitemap.xml and chat-app preview bots here; ng serve does the same with proxy.conf.json.
/// On IIS / Plesk the same content is written as files into the storefront folder instead (<see cref="SeoFilesWriter"/>).
/// </summary>
[Route("api/seo")]
public class SeoController(SeoBuilder seo, IMemoryCache cache) : ApiControllerBase
{
    [HttpGet("robots.txt")]
    [ResponseCache(Duration = 3600)]
    public ContentResult Robots() => Content(seo.Robots(), "text/plain; charset=utf-8");

    [HttpGet("sitemap.xml")]
    [ResponseCache(Duration = 3600)]
    public async Task<ContentResult> Sitemap()
    {
        var xml = await cache.GetOrCreateAsync("seo:sitemap", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1); // new products appear within the hour
            return await seo.SitemapAsync();
        });
        return Content(xml!, "application/xml; charset=utf-8");
    }

    [HttpGet("preview")]
    [ResponseCache(Duration = 600)]
    public async Task<ContentResult> Preview(string? path) => Content(await seo.PreviewAsync(path), "text/html; charset=utf-8");
}
