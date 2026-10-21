using System.Globalization;
using System.Security;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Seo;

/// <summary>
/// Builds the storefront's robots.txt, sitemap.xml and link-preview pages from the live catalogue and the configured
/// addresses (Smtp:StorefrontUrl, Smtp:ApiUrl). Served by <c>SeoController</c> (nginx setups) and written to the
/// storefront folder by <see cref="SeoFilesWriter"/> (IIS / Plesk setups).
/// </summary>
public class SeoBuilder(AppDbContext db, CatalogService catalog, IOptions<SmtpOptions> options)
{
    public const string SiteTitle = "WoodsyCart — Furniture in Bengaluru, factory-direct";
    public const string SiteDescription = "Buy beds, sofas, wardrobes, dining sets and office furniture online in Bengaluru at factory prices. Free delivery and assembly.";
    private static readonly CultureInfo India = new("en-IN");

    private string Site => options.Value.StorefrontUrl?.TrimEnd('/') ?? throw new InvalidOperationException("Smtp:StorefrontUrl is not configured.");
    /// <summary>Photos stored on this server ("/media/...") are linked through the public API address.</summary>
    private string? Api => options.Value.ApiUrl?.TrimEnd('/');

    /// <summary>Pages that are personal or have no search value.</summary>
    private static readonly string[] Private =
        ["/account", "/cart", "/checkout", "/wishlist", "/order/", "/login", "/register", "/forgot-password", "/reset-password", "/track", "/shop?q=", "/share/"];

    private static readonly string[] InfoPages =
        ["/about", "/faq", "/contact", "/policies/shipping", "/policies/returns", "/policies/privacy", "/policies/terms"];

    public string Robots()
    {
        var sb = new StringBuilder("# WoodsyCart storefront\nUser-agent: *\nAllow: /\n");
        foreach (var p in Private) sb.Append("Disallow: ").Append(p).Append('\n');
        sb.Append("\nSitemap: ").Append(Site).Append("/sitemap.xml\n");
        return sb.ToString();
    }

    public async Task<string> SitemapAsync()
    {
        var now = DateTime.UtcNow;
        var products = await catalog.Visible().OrderBy(p => p.Id)
            .Select(p => new
            {
                p.Slug, Modified = p.UpdatedAt ?? p.CreatedAt,
                Images = p.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).Select(i => i.LocalPath ?? i.Url).Take(5).ToList()
            })
            .ToListAsync();
        var visible = catalog.Visible();
        var categories = await db.Categories.AsNoTracking()
            .Where(c => c.IsActive && visible.Any(p => p.CategoryId == c.Id)).OrderBy(c => c.SortOrder).Select(c => c.Slug).ToListAsync();
        var offers = await LiveOfferSlugsAsync();
        var newest = products.Count > 0 ? products.Max(p => p.Modified) : now;

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
          .Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\" xmlns:image=\"http://www.google.com/schemas/sitemap-image/1.1\">\n");
        Url(sb, "/", newest, "daily", "1.0");
        Url(sb, "/shop", newest, "daily", "0.9");
        foreach (var c in categories) Url(sb, "/shop?category=" + Uri.EscapeDataString(c), newest, "daily", "0.8");
        if (offers.Count > 0) Url(sb, "/offers", now, "daily", "0.8");
        foreach (var o in offers) Url(sb, "/offers/" + Uri.EscapeDataString(o), now, "daily", "0.7");
        foreach (var p in products)
        {
            Url(sb, "/product/" + Uri.EscapeDataString(p.Slug), p.Modified, "weekly", "0.7", close: false);
            foreach (var img in p.Images.Select(Absolute).OfType<string>())
                sb.Append("    <image:image><image:loc>").Append(Esc(img)).Append("</image:loc></image:image>\n");
            sb.Append("  </url>\n");
        }
        foreach (var page in InfoPages) Url(sb, page, null, "monthly", "0.3");
        sb.Append("</urlset>\n");
        return sb.ToString();
    }

    public Task<List<string>> ProductSlugsAsync() => catalog.Visible().OrderBy(p => p.Id).Select(p => p.Slug).ToListAsync();

    public Task<List<string>> LiveOfferSlugsAsync()
    {
        var now = DateTime.UtcNow;
        return db.Offers.AsNoTracking().Where(o => o.IsActive && o.StartsAt <= now && o.EndsAt >= now).Select(o => o.Slug).ToListAsync();
    }

    /// <summary>
    /// A tiny page with the title, description and photo for <paramref name="path"/> (a product, an offer, or anything
    /// else → the shop's own). Chat apps build link previews without running JavaScript, so they read this instead of the app.
    /// </summary>
    public async Task<string> PreviewAsync(string? path)
    {
        path = "/" + (path ?? "").Trim().TrimStart('/');
        var url = Site + (path == "/" ? "/" : path);
        string title = SiteTitle, description = SiteDescription, type = "website";
        string? image = null, imageAlt = null, price = null;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is ["product", var slug] && await catalog.GetProductAsync(Uri.UnescapeDataString(slug)) is { } p)
        {
            var photo = p.Images.Where(i => i.MediaType != (int)MediaType.Video).Select(i => i.Url).FirstOrDefault();
            var prices = p.Variants.Where(v => v.Price > 0).Select(v => v.Price).DefaultIfEmpty(p.Price).ToList();
            var from = prices.Min() < prices.Max() ? "From " : "";
            var highlights = (p.Description ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2).Take(3);
            title = p.MetaTitle ?? p.Name;
            description = $"{from}₹{p.Price.ToString("N0", India)}" + (p.DiscountPercent >= 3 ? $" ({p.DiscountPercent}% off)" : "") +
                          " · Free delivery & assembly in Bengaluru. " + (p.MetaDescription ?? string.Join(" ", highlights));
            image = photo is null ? null : Absolute(photo);
            imageAlt = p.Name;
            type = "product";
            price = p.Price.ToString(CultureInfo.InvariantCulture);
            url = $"{Site}/product/{Uri.EscapeDataString(p.Slug)}";
        }
        else if (segments is ["offers", var offerSlug] && await catalog.GetOfferAsync(Uri.UnescapeDataString(offerSlug)) is { } o)
        {
            title = o.Name;
            description = o.Tagline ?? o.Description ?? description;
            image = o.BannerImageUrl is null ? null : Absolute(o.BannerImageUrl);
        }
        image ??= Site + "/icons/icon-512x512.png";
        description = description.Length > 200 ? description[..199].TrimEnd() + "…" : description;

        var e = (string s) => System.Net.WebUtility.HtmlEncode(s);
        var sb = new StringBuilder("<!doctype html>\n<html lang=\"en-IN\"><head><meta charset=\"utf-8\">\n");
        sb.Append($"<title>{e(title == SiteTitle ? title : title + " · WoodsyCart")}</title>\n");
        sb.Append($"<meta name=\"description\" content=\"{e(description)}\">\n<link rel=\"canonical\" href=\"{e(url)}\">\n");
        void Og(string prop, string? value) { if (value is not null) sb.Append($"<meta property=\"{prop}\" content=\"{e(value)}\">\n"); }
        Og("og:site_name", "WoodsyCart"); Og("og:locale", "en_IN"); Og("og:type", type); Og("og:url", url);
        Og("og:title", title); Og("og:description", description); Og("og:image", image); Og("og:image:alt", imageAlt);
        Og("product:price:amount", price); Og("product:price:currency", price is null ? null : "INR");
        sb.Append($"<meta name=\"twitter:card\" content=\"summary_large_image\">\n<meta name=\"twitter:title\" content=\"{e(title)}\">\n");
        sb.Append($"<meta name=\"twitter:description\" content=\"{e(description)}\">\n<meta name=\"twitter:image\" content=\"{e(image)}\">\n");
        // A person who somehow lands here goes straight to the real page.
        sb.Append($"<meta http-equiv=\"refresh\" content=\"0;url={e(url)}\">\n</head><body><a href=\"{e(url)}\">{e(title)}</a></body></html>\n");
        return sb.ToString();
    }

    private void Url(StringBuilder sb, string path, DateTime? modified, string freq, string priority, bool close = true)
    {
        sb.Append("  <url><loc>").Append(Esc(Site + path)).Append("</loc>");
        if (modified is not null) sb.Append("<lastmod>").Append(modified.Value.ToString("yyyy-MM-dd")).Append("</lastmod>");
        sb.Append("<changefreq>").Append(freq).Append("</changefreq><priority>").Append(priority).Append("</priority>");
        sb.Append(close ? "</url>\n" : "\n");
    }

    /// <summary>Vendor CDN photos are already absolute; our own copies need the public API address.</summary>
    private string? Absolute(string url) =>
        url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ? null // a video link, not a photo
        : url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : Api is null ? null : Api + url;

    private static string Esc(string s) => SecurityElement.Escape(s);
}
