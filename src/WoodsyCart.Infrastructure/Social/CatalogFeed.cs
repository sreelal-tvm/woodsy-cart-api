using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Social;

public record CatalogFeedStatus(bool Enabled, string? Url, int Products, int Ready, DateTime? FetchedAt, string? FetchedBy);

/// <summary>
/// The product feed Meta Commerce Manager reads daily into a catalog (Instagram/Facebook Shop, catalog ads): one CSV row per
/// published product with price, sale price, stock, link and photos. Meta only takes JPEG/PNG, so the white-background JPEGs
/// made for posts are used; they are made in the background (<see cref="PrepareImagesAsync"/>) and a product joins the feed
/// once its photos are ready.
/// </summary>
public partial class CatalogFeed(AppDbContext db, SocialService social, IOptions<SmtpOptions> urls, ILogger<CatalogFeed> logger)
{
    public const string Path = "/feeds/meta-catalog.csv";
    private static readonly string[] Columns =
    [
        "id", "title", "description", "availability", "condition", "price", "sale_price", "link", "image_link", "additional_image_link",
        "brand", "google_product_category", "product_type", "color", "material", "custom_label_0"
    ];

    private IQueryable<Product> Published() => db.Products.AsNoTracking().Where(p => p.IsActive && !p.IsDeleted
        && p.Images.Any(i => i.MediaType == MediaType.Image && i.LocalPath != null));

    public async Task<CatalogFeedStatus> StatusAsync()
    {
        var c = await social.ConfigAsync();
        var products = await Published().Include(p => p.Images).AsSplitQuery().ToListAsync();
        var ready = products.Count(p => social.ReadyJpegs(p) is not null);
        return new CatalogFeedStatus(c.FeedEnabled, Url(), products.Count, ready, c.FeedFetchedAt, c.FeedFetchedBy);
    }

    /// <summary>Makes the JPEGs of up to <paramref name="max"/> products that don't have them yet (worker, every 10 minutes).</summary>
    public async Task<int> PrepareImagesAsync(int max, CancellationToken ct)
    {
        var c = await social.ConfigAsync();
        if (!c.FeedEnabled) return 0;
        var made = 0;
        foreach (var p in await Published().Include(p => p.Images).OrderBy(p => p.Id).AsSplitQuery().ToListAsync(ct))
        {
            if (made >= max || ct.IsCancellationRequested) break;
            if (social.ReadyJpegs(p) is not null) continue;
            try { social.PrepareJpegsFor(p); made++; }
            catch (IOException ex) { logger.LogWarning("Feed photos for product {Product} failed: {Message}", p.Id, ex.Message); }
        }
        return made;
    }

    /// <summary>The CSV (only products whose JPEGs are ready). Records who fetched it, to show in Admin.</summary>
    public async Task<string?> CsvAsync(string? userAgent)
    {
        var c = await social.ConfigAsync();
        if (!c.FeedEnabled) return null;
        c.FeedFetchedAt = DateTime.UtcNow;
        c.FeedFetchedBy = userAgent is { Length: > 200 } ? userAgent[..200] : userAgent;
        await db.SaveChangesAsync();

        var api = urls.Value.ApiUrl?.TrimEnd('/');
        var store = urls.Value.StorefrontUrl?.TrimEnd('/') ?? "https://woodsycart.com";
        var biz = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var products = await Published().Include(p => p.Category).Include(p => p.Images)
            .Include(p => p.Options).ThenInclude(o => o.Values).OrderBy(p => p.Id).AsSplitQuery().ToListAsync();
        var inr = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns));
        foreach (var p in products)
        {
            var photos = social.ReadyJpegs(p);
            if (photos is null || photos.Count == 0) continue;
            var now = p.OfferPrice ?? p.SellingPrice;
            var regular = p.Mrp > now ? p.Mrp : now;
            var colour = p.Options.Where(o => o.IsColour).SelectMany(o => o.Values.Where(v => !v.IsHidden).OrderBy(v => v.SortOrder))
                .Select(v => v.Value.Trim()).FirstOrDefault(v => v.Length > 0);
            var category = p.Category?.Name ?? "";
            var row = new[]
            {
                SocialCaption.Code(p.Id),
                Cut(p.Name.Trim(), 200),
                Cut(Description(p, biz), 9999),
                p.IsAvailable ? "in stock" : "out of stock",
                "new",
                regular.ToString("0.00", inr) + " INR",
                regular > now ? now.ToString("0.00", inr) + " INR" : "",
                $"{store}/product/{p.Slug}",
                api + photos[0],
                string.Join(",", photos.Skip(1).Select(x => api + x)),
                Cut(string.IsNullOrWhiteSpace(p.Brand) ? biz.BusinessName : p.Brand.Trim(), 100),
                GoogleCategory(category),
                category,
                colour ?? "",
                Cut(Label().Replace(p.Material?.Trim() ?? "", ""), 200),
                category
            };
            sb.AppendLine(string.Join(",", row.Select(Quote)));
        }
        return sb.ToString();
    }

    public string? Url() => urls.Value.ApiUrl is { Length: > 0 } a && a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? a.TrimEnd('/') + Path : null;

    /// <summary>Plain text (Meta refuses HTML and links) that differs from the title.</summary>
    private static string Description(Product p, BusinessSettings biz)
    {
        var text = (p.Description ?? p.MetaDescription ?? "").Replace("\r", "");
        text = Links().Replace(Tags().Replace(text, " "), "");
        text = string.Join("\n", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        // Size and warranty only when the description doesn't already say them
        var said = Simple(text);
        var size = p.Dimensions is { Length: > 0 } d ? Label().Replace(d.Trim(), "") : null;
        var warranty = p.Warranty is { Length: > 0 } w ? Label().Replace(w.Trim(), "") : null;
        var extra = new[]
            {
                size is not null && !said.Contains(Simple(size)) ? "Size: " + size : null,
                warranty is not null && !said.Contains(Simple(warranty)) ? warranty : null,
                p.AssemblyIncluded ? $"Delivered and assembled at your home in {biz.City ?? "Bengaluru"} in {p.DeliveryDays} days." : null
            }.Where(x => x is not null);
        text = string.Join("\n", new[] { text }.Concat(extra!).Where(x => x!.Length > 0));
        return text.Length > 0 && !text.Equals(p.Name.Trim(), StringComparison.OrdinalIgnoreCase) ? text : $"{p.Name.Trim()} from {biz.BusinessName}.";
    }

    /// <summary>Google product category by our category name (Meta reads Google's names); Furniture when unsure.</summary>
    private static string GoogleCategory(string name)
    {
        var n = name.ToLowerInvariant();
        return n switch
        {
            _ when n.Contains("mattress") => "Furniture > Beds & Accessories > Mattresses",
            _ when n.Contains("sofa") => "Furniture > Sofas",
            _ when n.Contains("bedroom set") => "Furniture > Furniture Sets > Bedroom Furniture Sets",
            _ when n.Contains("dining") => "Furniture > Furniture Sets > Kitchen & Dining Furniture Sets",
            _ when n.Contains("bed") => "Furniture > Beds & Accessories > Beds & Bed Frames",
            _ when n.Contains("chair") => "Furniture > Chairs",
            _ when n.Contains("wardrobe") => "Furniture > Cabinets & Storage > Armoires & Wardrobes",
            _ when n.Contains("storage") || n.Contains("cabinet") => "Furniture > Cabinets & Storage",
            _ when n.Contains("tv") => "Furniture > Entertainment Centers & TV Stands",
            _ when n.Contains("center table") || n.Contains("centre table") => "Furniture > Tables > Accent Tables > Coffee Tables",
            _ when n.Contains("office") || n.Contains("computer") || n.Contains("reception") => "Furniture > Office Furniture",
            _ when n.Contains("table") => "Furniture > Tables",
            _ => "Furniture"
        };
    }

    private static string Simple(string s) => NonWord().Replace(s.ToLowerInvariant(), "");
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
    private static string Quote(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"^\s*(size|material|colou?r|warranty)\s*[:\-]+\s*", RegexOptions.IgnoreCase)] private static partial Regex Label();
    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NonWord();
    [GeneratedRegex(@"https?://\S+|www\.\S+", RegexOptions.IgnoreCase)] private static partial Regex Links();
}
