using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WoodsyCart.Domain.Entities;

namespace WoodsyCart.Infrastructure.Social;

/// <summary>Everything a post is built from; also stored with the post to show what changed later.</summary>
public record PostContent(string Caption, List<string> Photos, Dictionary<string, string> Snapshot)
{
    /// <summary>Caption + photos. A different hash means the live post no longer matches the product.</summary>
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Caption + "\n" + string.Join("\n", Photos))))[..32];
}

/// <summary>
/// Builds the Instagram / Facebook caption (or the shorter Threads text) from the product. Template tokens are {name}; a line whose token is empty
/// is left out, so nothing ever shows blank.
/// </summary>
public static partial class SocialCaption
{
    public const int MaxLength = 2200;   // Instagram's caption limit
    public const int MaxPhotos = 10;     // Instagram carousel limit
    public const int ThreadsMaxLength = 500;   // Threads' text limit, emojis counted by their UTF-8 bytes

    public const string DefaultThreadsTemplate =
        """
        {name} | {category} in {city} 🛋️

        {price}
        ✅ Colour: {colours}
        📐 Size: {dimensions}
        🚚 Delivered in {delivery_days} days{assembly}

        🛒 {url}
        💬 WhatsApp: {whatsapp}
        """;

    public const string DefaultTemplate =
        """
        {name} | {category} in {city} 🛋️
        {hook}

        {price}
        ✅ Colour: {colours}
        📐 Size: {dimensions}
        🪵 Material: {material}
        🛡️ Warranty: {warranty}
        🚚 Delivered in {delivery_days} days{assembly}

        {details}

        {variants}

        🛒 Order at {website} (link in bio)
        🔎 Search "{search}" or code {code} on our website
        💬 WhatsApp: {whatsapp}

        {hashtags}
        """;

    public static readonly string[] Tokens =
    [
        "name", "category", "city", "hook", "price", "colours", "dimensions", "material", "warranty", "delivery_days", "assembly",
        "details", "variants", "website", "url", "search", "code", "whatsapp", "hashtags"
    ];

    /// <param name="threads">Threads: its own short template, no "link in bio" (links work there), at most 500 characters.</param>
    /// <param name="storeUrl">Storefront address for {url}, e.g. https://woodsycart.com</param>
    /// <param name="tagWords">The hashtags' words again in brackets under them, e.g. [FurnitureBangalore, HomeDecor] (from 29 Oct 2026;
    /// false = the format of earlier posts).</param>
    /// <param name="linkSource">Added to the {url} link as ?utm_source=…, so Admin → Visitors can tell where visitors came from
    /// (from 30 Oct 2026; null = the format of earlier posts).</param>
    public static PostContent Build(Product p, SocialConfig cfg, BusinessSettings biz, string? overrideCaption, List<string> photos,
        bool threads = false, string? storeUrl = null, bool tagWords = true, string? linkSource = null)
    {
        var inr = CultureInfo.GetCultureInfo("en-IN");
        string Money(decimal v) => "₹" + v.ToString("#,##0", inr);
        var city = Clean(cfg.City) ?? Clean(biz.City) ?? "Bengaluru";

        var variants = p.Variants.Where(v => v.IsActive && v.IsAvailable).ToList();
        var price = p.OfferPrice ?? p.SellingPrice;
        var minVariant = variants.Count > 1 ? variants.Min(v => v.OfferPrice ?? v.SellingPrice) : price;
        var hasRange = variants.Count > 1 && variants.Select(v => v.OfferPrice ?? v.SellingPrice).Distinct().Count() > 1;
        var shown = hasRange ? Math.Min(minVariant, price) : price;
        string priceLine;
        if (!cfg.IncludePrice) priceLine = threads ? "💰 Price on our website" : "💰 Price on our website (link in bio)";
        else
        {
            priceLine = $"💰 {(hasRange ? "From " : "")}{Money(shown)}";
            if (!hasRange && cfg.ShowMrp && p.Mrp > shown) priceLine += $" (MRP {Money(p.Mrp)}, you save {Money(p.Mrp - shown)})";
            if (p.OfferPrice is not null && p.Offer is { Name.Length: > 0 }) priceLine = $"🎉 {p.Offer.Name}\n{priceLine}";
        }

        var options = p.Options.OrderBy(o => o.Position).ToList();
        var colours = options.Where(o => o.IsColour).SelectMany(o => o.Values.Where(v => !v.IsHidden).OrderBy(v => v.SortOrder).Select(v => v.Value.Trim()))
            .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var variantLines = options.Where(o => !o.IsColour)
            .Select(o => (o.Name, Values: o.Values.Where(v => !v.IsHidden).OrderBy(v => v.SortOrder).Select(v => v.Value.Trim()).Where(v => v.Length > 0).Distinct().ToList()))
            .Where(o => o.Values.Count > 1)
            .Select(o => $"🔸 {o.Name}: {string.Join(" · ", o.Values)}").ToList();

        var known = new[] { p.Dimensions, p.Material, p.Warranty }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Norm(x!)).ToList();
        var details = (p.Description ?? "").Replace("\r", "").Split('\n')
            .Select(l => BulletPrefix().Replace(l, "").Trim())
            .Where(l => l.Length >= 3 && !known.Any(k => k.Contains(Norm(l)) || Norm(l).Contains(k)))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6)
            .Select(l => "• " + Sentence(l)).ToList();

        var website = Clean(biz.Website) is { } w ? SchemePrefix().Replace(w, "").TrimEnd('/') : "woodsycart.com";
        var url = $"{(Clean(storeUrl) ?? "https://" + website).TrimEnd('/')}/product/{p.Slug}{(linkSource is null ? "" : "?utm_source=" + linkSource)}";
        var hook = Clean(p.MetaDescription)
            ?? $"{p.Name}{(colours.Count > 0 ? " in " + string.Join(", ", colours) : "")}, {(p.AssemblyIncluded ? "delivered and assembled at your home" : "delivered to your home")} in {city}.";

        var values = new Dictionary<string, string>
        {
            ["name"] = p.Name.Trim(),
            ["category"] = p.Category?.Name.Trim() ?? "",
            ["city"] = city,
            ["hook"] = hook,
            ["price"] = priceLine,
            ["colours"] = string.Join(" · ", colours),
            ["dimensions"] = Clean(p.Dimensions) is { } d ? SizePrefix().Replace(d, "").Trim() : "",
            ["material"] = Clean(p.Material) is { } m ? MadeOfPrefix().Replace(m, "").Trim() : "",
            ["warranty"] = Clean(p.Warranty) ?? "",
            ["delivery_days"] = p.DeliveryDays > 0 ? p.DeliveryDays.ToString(inr) : "",
            ["assembly"] = p.AssemblyIncluded ? " · assembly included" : "",
            ["details"] = string.Join("\n", details),
            ["variants"] = string.Join("\n", variantLines),
            ["website"] = website,
            ["url"] = url,
            ["search"] = p.Name.Trim().ToLowerInvariant(),
            ["code"] = Code(p.Id),
            ["whatsapp"] = Phone(cfg.WhatsApp ?? biz.WhatsApp),
            ["hashtags"] = Hashtags(p, cfg, city, tagWords)
        };

        var caption = string.IsNullOrWhiteSpace(overrideCaption)
            ? Render(threads ? cfg.ThreadsTemplate ?? DefaultThreadsTemplate : cfg.Template ?? DefaultTemplate, values)
            : overrideCaption.Trim();
        if (threads) caption = FitThreads(caption, url);
        else if (caption.Length > MaxLength) caption = caption[..(MaxLength - 1)].TrimEnd() + "…";

        var snapshot = new Dictionary<string, string>
        {
            ["Name"] = values["name"], ["Price"] = cfg.IncludePrice ? Money(shown) : "", ["MRP"] = cfg.IncludePrice && cfg.ShowMrp ? Money(p.Mrp) : "",
            ["Offer"] = p.OfferPrice is not null ? p.Offer?.Name ?? "" : "", ["Colours"] = values["colours"], ["Options"] = values["variants"],
            ["Size"] = values["dimensions"], ["Material"] = values["material"], ["Warranty"] = values["warranty"], ["Delivery"] = values["delivery_days"],
            ["Description"] = values["details"], ["Photos"] = string.Join(",", photos.Select(Path.GetFileName)), ["Caption"] = caption
        };
        return new PostContent(caption, photos, snapshot);
    }

    public static string Code(int productId) => $"WC{productId}";

    /// <summary>The words on the reel's slides (short: they are read in a few seconds each).</summary>
    public static ReelScript Reel(Product p, SocialConfig cfg, BusinessSettings biz)
    {
        var inr = CultureInfo.GetCultureInfo("en-IN");
        string Money(decimal v) => "₹" + v.ToString("#,##0", inr);
        var variants = p.Variants.Where(v => v.IsActive && v.IsAvailable).ToList();
        var price = p.OfferPrice ?? p.SellingPrice;
        var hasRange = variants.Count > 1 && variants.Select(v => v.OfferPrice ?? v.SellingPrice).Distinct().Count() > 1;
        var shown = hasRange ? Math.Min(variants.Min(v => v.OfferPrice ?? v.SellingPrice), price) : price;
        var showMrp = cfg.IncludePrice && cfg.ShowMrp && !hasRange && p.Mrp > shown;

        var colours = p.Options.Where(o => o.IsColour).SelectMany(o => o.Values.Where(v => !v.IsHidden).OrderBy(v => v.SortOrder).Select(v => v.Value.Trim()))
            .Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var facts = new List<string>();
        if (colours.Count > 0) facts.Add((colours.Count == 1 ? "Colour: " : "Colours: ") + string.Join(", ", colours.Take(4)));
        if (Clean(p.Dimensions) is { } d) facts.Add("Size: " + SizePrefix().Replace(d, "").Trim());
        if (Clean(p.Material) is { } m) facts.Add(Sentence(MadeOfPrefix().Replace(m, "").Trim()));
        if (Clean(p.Warranty) is { } w) facts.Add(Sentence(w));
        foreach (var o in p.Options.Where(o => !o.IsColour).OrderBy(o => o.Position))
        {
            var vals = o.Values.Where(v => !v.IsHidden).OrderBy(v => v.SortOrder).Select(v => v.Value.Trim()).Where(v => v.Length > 0).Distinct().ToList();
            if (vals.Count > 1) facts.Add($"{o.Name}: {string.Join(", ", vals.Take(4))}");
        }
        if (p.DeliveryDays > 0) facts.Add(p.AssemblyIncluded ? $"Delivered & assembled in {p.DeliveryDays} days" : $"Delivered in {p.DeliveryDays} days");

        var website = Clean(biz.Website) is { } site ? SchemePrefix().Replace(site, "").TrimEnd('/') : "woodsycart.com";
        var phone = Phone(cfg.WhatsApp ?? biz.WhatsApp);
        return new ReelScript(p.Name.Trim(),
            cfg.IncludePrice ? (hasRange ? "From " : "") + Money(shown) : null,
            showMrp ? Money(p.Mrp) : null, showMrp ? Money(p.Mrp - shown) : null,
            p.OfferPrice is not null ? Clean(p.Offer?.Name) : null,
            facts, website, phone.Length > 0 ? phone : null, Code(p.Id), Clean(biz.Tagline));
    }

    /// <summary>Length as Threads counts it: emojis and symbols such as ₹ by their UTF-8 bytes.</summary>
    public static int ThreadsLength(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>Over 500: drop lines from the bottom up (keeping the first line and the product link), then cut.</summary>
    private static string FitThreads(string text, string url)
    {
        var lines = text.Split('\n').ToList();
        for (var i = lines.Count - 1; ThreadsLength(string.Join("\n", lines)) > ThreadsMaxLength && i > 0; i--)
            if (!lines[i].Contains(url)) lines.RemoveAt(i);
        text = BlankRuns().Replace(string.Join("\n", lines), "\n\n").Trim();
        if (ThreadsLength(text) <= ThreadsMaxLength) return text;
        while (ThreadsLength(text) + 3 > ThreadsMaxLength || char.IsHighSurrogate(text[^1])) text = text[..^1];
        return text.TrimEnd() + "…";
    }

    /// <summary>Names of the details that differ between what was posted and the product now.</summary>
    public static List<string> Changes(string? postedSnapshot, Dictionary<string, string> now)
    {
        if (string.IsNullOrEmpty(postedSnapshot)) return [];
        var before = JsonSerializer.Deserialize<Dictionary<string, string>>(postedSnapshot) ?? [];
        var changed = now.Where(kv => kv.Key != "Caption" && before.GetValueOrDefault(kv.Key) != kv.Value).Select(kv => kv.Key).ToList();
        if (changed.Count == 0 && before.GetValueOrDefault("Caption") != now.GetValueOrDefault("Caption")) changed.Add("Caption");
        return changed;
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var lines = new List<string>();
        foreach (var raw in template.Replace("\r", "").Split('\n'))
        {
            var empty = false;
            var line = TokenPattern().Replace(raw, m =>
            {
                var key = m.Groups[1].Value.ToLowerInvariant();
                if (!values.TryGetValue(key, out var v)) return m.Value;   // unknown token stays visible, so a typo is noticed
                if (string.IsNullOrWhiteSpace(v)) empty = true;
                return v;
            });
            if (!empty) lines.Add(line.TrimEnd());
        }
        var text = string.Join("\n", lines);
        return BlankRuns().Replace(text, "\n\n").Trim();
    }

    private static string Hashtags(Product p, SocialConfig cfg, string city, bool tagWords)
    {
        var cityTag = city.Equals("Bengaluru", StringComparison.OrdinalIgnoreCase) ? "Bangalore" : Tag(city);
        var category = p.Category is null ? "" : Tag(p.Category.Name);
        var tags = new List<string> { "WoodsyCart" };
        if (category.Length > 0) { tags.Add(category); tags.Add(category + cityTag); }
        tags.AddRange((cfg.Hashtags ?? "").Split([' ', ',', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(Tag));
        tags.AddRange(p.Tags.Select(t => Tag(t.Name)));
        var used = tags.Where(t => t.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(cfg.MaxHashtags, 1, 30)).ToList();
        var line = string.Join(" ", used.Select(t => "#" + t));
        return tagWords && used.Count > 0 ? $"{line}\n[{string.Join(", ", used)}]" : line;
    }

    private static string Tag(string s) =>
        string.Concat(NonWord().Split(s.TrimStart('#')).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    private static string Phone(string? raw)
    {
        var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 10) digits = "91" + digits;
        return digits.Length == 12 && digits.StartsWith("91") ? $"+91 {digits[2..7]} {digits[7..]}" : (raw ?? "").Trim();
    }

    private static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    private static string Norm(string s) => NonWord().Replace(s.ToLowerInvariant(), "");
    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"\{([a-zA-Z_]+)\}")] private static partial Regex TokenPattern();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex BlankRuns();
    // Leading bullets and dashes (a dot only when a space follows, so ".45 mm" keeps its decimal point)
    [GeneratedRegex(@"^(?:[\s\-–•*·]+|\.(?=\s))+")] private static partial Regex BulletPrefix();
    [GeneratedRegex(@"^https?://", RegexOptions.IgnoreCase)] private static partial Regex SchemePrefix();
    [GeneratedRegex(@"^size\s*[:\-]?\s*", RegexOptions.IgnoreCase)] private static partial Regex SizePrefix();
    [GeneratedRegex(@"^made\s+of\s+", RegexOptions.IgnoreCase)] private static partial Regex MadeOfPrefix();
    [GeneratedRegex(@"[^\p{L}\p{N}]+")] private static partial Regex NonWord();
}
