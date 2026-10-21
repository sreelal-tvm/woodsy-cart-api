using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Vendors;

public class SyncOptions
{
    public bool DownloadImages { get; set; } = true;
    /// <summary>Publish newly found vendor products immediately (otherwise they wait for review in Admin).</summary>
    public bool PublishNewProducts { get; set; } = true;
    /// <summary>Vendor collections meaning "deliverable in our city".</summary>
    public string[] HomeCityCollections { get; set; } = ["Banglore", "Bengaluru", "Bangalore", "All India Delivery"];
    /// <summary>Vendor collections for other branches; products only in these are imported as drafts.</summary>
    public string[] OtherCityCollections { get; set; } = ["Ahmedabad", "Surat", "Vadodara", "Pune", "Kanpur", "Unnao", "Hyderabad", "Mumbai"];
}

/// <summary>Live progress of the running sync, polled by the admin UI.</summary>
public class SyncProgress
{
    public bool IsRunning { get; set; }
    public string Stage { get; set; } = "Idle";
    public int Total { get; set; }
    public int Processed { get; set; }
    public int? LogId { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? LastError { get; set; }
}

public partial class VendorSyncService(
    AppDbContext db,
    DotShowroomClient client,
    IHttpClientFactory httpFactory,
    FileStorage storage,
    SyncProgress progress,
    Images.ImageCleaningQueue cleaningQueue,
    EmailSender email,
    ILogger<VendorSyncService> logger)
{
    public async Task RunAsync(int vendorId, SyncOptions options, CancellationToken ct)
    {
        var vendor = await db.Vendors.FindAsync([vendorId], ct) ?? throw new AppException("Vendor not found", 404);
        if (vendor.ExternalStoreId is null) throw new AppException("Vendor has no external store id configured.");
        var storeId = vendor.ExternalStoreId.Value;

        var log = new VendorSyncLog { VendorId = vendorId, Status = SyncStatus.Running };
        db.VendorSyncLogs.Add(log);
        await db.SaveChangesAsync(ct);
        progress.LogId = log.Id;
        progress.StartedAt = DateTime.UtcNow;
        progress.LastError = null;

        try
        {
            // 1. Catalogue listing (categories + item ids)
            progress.Stage = "Reading vendor catalogue";
            var catalog = await client.GetCatalogAsync(storeId, ct);
            var itemCategory = new Dictionary<long, DsCategory>();
            var listed = new Dictionary<long, DsItem>();
            foreach (var c in catalog)
                foreach (var i in c.Items ?? [])
                {
                    itemCategory.TryAdd(i.Id, c.Category);
                    listed.TryAdd(i.Id, i);
                }

            progress.Total = itemCategory.Count;
            progress.Processed = 0;
            log.ProductsFound = itemCategory.Count;

            // 2. Product details (variants, all images, collections) — throttled to be polite to the vendor's API
            progress.Stage = "Downloading product details";
            var details = new ConcurrentDictionary<long, DsItem>();
            using (var gate = new SemaphoreSlim(4))
            {
                var tasks = itemCategory.Keys.Select(async id =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var item = await RetryAsync(() => client.GetItemAsync(storeId, id, ct), ct);
                        details[id] = item ?? listed[id];
                    }
                    catch (Exception ex)
                    {
                        // Some listings have no detail page (renamed/redirected); the listing data still has price, images and description.
                        logger.LogInformation("Vendor item {Id} has no detail page ({Msg}); using listing data", id, ex.Message);
                        details[id] = listed[id];
                    }
                    finally
                    {
                        gate.Release();
                        lock (progress) progress.Processed++;
                    }
                });
                await Task.WhenAll(tasks);
            }

            // 3. Reference data
            progress.Stage = "Updating categories & collections";
            var categoryMap = await UpsertCategoriesAsync(catalog.Select(c => c.Category).ToList(), vendor, ct);
            var collectionMap = await UpsertCollectionsAsync(details.Values.SelectMany(d => d.Collections ?? []).ToList(), ct);
            var tagMap = await UpsertTagsAsync(details.Values.SelectMany(d => d.Tags ?? []).ToList(), ct);

            // 4. Products
            progress.Stage = "Updating products";
            progress.Total = details.Count;
            progress.Processed = 0;
            var settings = await db.BusinessSettings.AsNoTracking().FirstAsync(ct);
            var slugs = (await db.Products.IgnoreQueryFilters().Select(p => p.Slug).ToListAsync(ct)).ToHashSet();
            var seen = new HashSet<long>();
            var batch = 0;

            foreach (var item in details.Values)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(item.Id);
                var catVendorId = item.Category?.Id ?? itemCategory[item.Id].Id;
                if (!categoryMap.TryGetValue(catVendorId, out var categoryId))
                    categoryId = categoryMap[itemCategory[item.Id].Id];

                var (added, priceChanged) = await UpsertProductAsync(item, vendor, categoryId, collectionMap, tagMap, settings, slugs, options, ct);
                if (added) log.ProductsAdded++; else log.ProductsUpdated++;
                if (priceChanged) log.PriceChanges++;

                progress.Processed++;
                if (++batch % 25 == 0)
                {
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                }
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            // 5. Products that disappeared from the vendor store become unavailable (kept for order history).
            var missing = await db.Products
                .Where(p => p.VendorId == vendorId && p.Source == ProductSource.Vendor && p.VendorProductId != null && p.IsAvailable)
                .ToListAsync(ct);
            foreach (var p in missing.Where(p => !seen.Contains(p.VendorProductId!.Value)))
            {
                p.IsAvailable = false;
                log.ProductsDeactivated++;
            }
            await db.SaveChangesAsync(ct);

            // 6. Images
            if (options.DownloadImages)
                log.ImagesDownloaded = await DownloadImagesAsync(vendorId, ct);

            log.Status = SyncStatus.Completed;
            log.Message = $"Synced {details.Count} of {itemCategory.Count} products.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Vendor sync failed");
            log.Status = SyncStatus.Failed;
            log.Message = ex.Message;
            progress.LastError = ex.Message;
        }
        finally
        {
            log.FinishedAt = DateTime.UtcNow;
            db.ChangeTracker.Clear();
            db.VendorSyncLogs.Update(log);
            await db.SaveChangesAsync(CancellationToken.None);
            if (log.Status == SyncStatus.Failed || log.PriceChanges > 0 || log.ProductsAdded > 0)
                await email.AdminsAsync(Email.EmailEvent.AdminVendorSync,
                    log.Status == SyncStatus.Failed ? "Vendor sync failed" : $"Vendor sync: {log.PriceChanges} price changes, {log.ProductsAdded} new products",
                    log.Status == SyncStatus.Failed ? "The vendor sync failed" : "Vendor sync finished",
                    log.Status == SyncStatus.Failed
                        ? $"<p>The catalogue sync stopped with an error:</p>{Email.EmailTemplates.Info(Email.EmailTemplates.E(log.Message), "#fef2f2", "#ef4444")}<p>Try <b>Sync now</b> again later; the store keeps the last good data.</p>"
                        : $"<p>Found {log.ProductsFound} products: <b>{log.ProductsAdded}</b> new, {log.ProductsUpdated} updated, <b>{log.PriceChanges}</b> price changes, {log.ProductsDeactivated} removed.</p>" +
                          (log.PriceChanges > 0 ? "<p>Selling prices were recalculated. Review the changes and accept them.</p>" : ""),
                    log.PriceChanges > 0 ? ("Review price changes", email.AdminUrl + "/products?status=price-changed") : ("Open products", email.AdminUrl + "/products"));
        }
    }

    private static async Task<T?> RetryAsync<T>(Func<Task<T?>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch when (attempt < 3) { await Task.Delay(1000 * attempt, ct); }
        }
    }

    private async Task<Dictionary<long, int>> UpsertCategoriesAsync(List<DsCategory> cats, Vendor vendor, CancellationToken ct)
    {
        var existing = await db.Categories.Where(c => c.VendorCategoryId != null).ToListAsync(ct);
        var order = 0;
        foreach (var c in cats)
        {
            order++;
            var name = c.Id == 0 ? "More Items" : c.Name.Trim();
            var cat = existing.FirstOrDefault(x => x.VendorCategoryId == c.Id);
            if (cat is null)
            {
                var slug = Slug.Create(name);
                if (await db.Categories.AnyAsync(x => x.Slug == slug, ct)) slug += "-" + c.Id;
                cat = new Category { Name = name, Slug = slug, VendorCategoryId = c.Id, SortOrder = order, ShowOnHome = c.Id != 0 };
                db.Categories.Add(cat);
                existing.Add(cat);
            }
            if (!string.IsNullOrEmpty(c.ImageUrl) && string.IsNullOrEmpty(cat.ImageUrl)) cat.ImageUrl = c.ImageUrl;
        }

        await db.SaveChangesAsync(ct);
        return existing.ToDictionary(x => x.VendorCategoryId!.Value, x => x.Id);
    }

    private async Task<Dictionary<long, int>> UpsertCollectionsAsync(List<DsCollection> cols, CancellationToken ct)
    {
        var existing = await db.Collections.Where(c => c.VendorCollectionId != null).ToListAsync(ct);
        foreach (var c in cols.DistinctBy(c => c.Id))
        {
            var col = existing.FirstOrDefault(x => x.VendorCollectionId == c.Id);
            if (col is null)
            {
                var name = c.Name.Trim() == "Banglore" ? "Bengaluru" : c.Name.Trim();
                var slug = Slug.Create(name);
                if (await db.Collections.AnyAsync(x => x.Slug == slug, ct)) slug += "-" + c.Id;
                col = new Collection { Name = name, Slug = slug, VendorCollectionId = c.Id, SortOrder = c.Rank };
                db.Collections.Add(col);
                existing.Add(col);
            }
            if (!string.IsNullOrEmpty(c.ImageUrl)) col.ImageUrl = c.ImageUrl;
        }
        await db.SaveChangesAsync(ct);
        return existing.ToDictionary(x => x.VendorCollectionId!.Value, x => x.Id);
    }

    private async Task<Dictionary<long, int>> UpsertTagsAsync(List<DsTag> tags, CancellationToken ct)
    {
        var existing = await db.Tags.Where(t => t.VendorTagId != null).ToListAsync(ct);
        foreach (var t in tags.DistinctBy(t => t.Id))
        {
            var tag = existing.FirstOrDefault(x => x.VendorTagId == t.Id);
            if (tag is null)
            {
                tag = new Tag { VendorTagId = t.Id };
                db.Tags.Add(tag);
                existing.Add(tag);
            }
            tag.Name = t.Name.Trim();
            if (!string.IsNullOrEmpty(t.TagColor)) tag.Color = t.TagColor;
        }
        await db.SaveChangesAsync(ct);
        return existing.ToDictionary(x => x.VendorTagId!.Value, x => x.Id);
    }

    private async Task<(bool added, bool priceChanged)> UpsertProductAsync(
        DsItem item, Vendor vendor, int categoryId,
        Dictionary<long, int> collectionMap, Dictionary<long, int> tagMap,
        BusinessSettings settings, HashSet<string> slugs, SyncOptions options, CancellationToken ct)
    {
        var p = await db.Products.IgnoreQueryFilters()
            .Include(x => x.Images)
            .Include(x => x.Options).ThenInclude(o => o.Values)
            .Include(x => x.Variants).ThenInclude(v => v.OptionValues)
            .Include(x => x.Collections).Include(x => x.Tags)
            .FirstOrDefaultAsync(x => x.VendorId == vendor.Id && x.VendorProductId == item.Id, ct);

        var added = p is null;
        if (p is null)
        {
            var slug = Slug.Create(item.Name, 70);
            if (!slugs.Add(slug)) { slug = $"{slug}-{item.Id % 100000}"; slugs.Add(slug); }
            p = new Product
            {
                Slug = slug,
                VendorId = vendor.Id,
                VendorProductId = item.Id,
                Source = ProductSource.Vendor,
                IsActive = options.PublishNewProducts,
                DeliveryDays = settings.DefaultDeliveryDays
            };
            db.Products.Add(p);
        }

        // Cost side is always refreshed from the vendor.
        var newCost = item.DiscountedPrice > 0 ? item.DiscountedPrice : item.Price;
        var priceChanged = !added && p.VendorPrice != newCost;
        if (priceChanged)
        {
            p.PreviousVendorPrice = p.VendorPrice;
            p.VendorPriceChanged = true;
        }
        p.VendorMrp = item.Price;
        p.VendorPrice = newCost;
        p.IsAvailable = item.Available == 1 && item.IsActive == 1;
        p.VendorUrl = item.Link;
        p.LastSyncedAt = DateTime.UtcNow;
        p.CategoryId = added || !p.LockContent ? categoryId : p.CategoryId;

        if (added || !p.LockContent)
        {
            p.Name = CleanName(item.Name);
            p.Description = item.Description?.Trim();
            p.DescriptionHtml = string.IsNullOrWhiteSpace(item.DescriptionDetail) ? null : item.DescriptionDetail;
            p.Brand = "WoodsyCart";
            p.HsnCode = item.Tax?.HsnSacCode;
            if (added) p.GstRate = item.Tax?.Gst is > 0 ? item.Tax.Gst : settings.DefaultGstRate;
            if (!string.IsNullOrWhiteSpace(item.SkuId)) p.Sku = item.SkuId;
            ExtractAttributes(p);
            SyncImages(p, item.Images ?? [], null);
        }

        SyncOptionsAndVariants(p, item);
        if (added || !p.LockContent)
            foreach (var v in item.Variants ?? [])
            {
                var variant = p.Variants.FirstOrDefault(x => x.VendorVariantId == v.VariantId);
                if (variant is not null && v.Images is { Count: > 0 }) SyncImages(p, v.Images, variant);
            }

        if (added || !p.LockContent)
        {
            var current = (item.Images ?? []).Concat((item.Variants ?? []).SelectMany(v => v.Images ?? []))
                .Select(i => i.ImageId).ToHashSet();
            foreach (var stale in p.Images.Where(i => i.VendorImageId != null && !current.Contains(i.VendorImageId.Value)).ToList())
            {
                storage.Delete(stale.LocalPath);
                storage.Delete(stale.LocalThumbnailPath);
                storage.Delete(stale.OriginalPath);
                storage.Delete(stale.OriginalThumbnailPath);
                p.Images.Remove(stale);
                db.ProductImages.Remove(stale);
            }
        }

        // Collections & tags
        var colIds = (item.Collections ?? []).Select(c => collectionMap.GetValueOrDefault(c.Id)).Where(i => i > 0).ToHashSet();
        var tagIds = (item.Tags ?? []).Select(t => tagMap.GetValueOrDefault(t.Id)).Where(i => i > 0).ToHashSet();
        var cols = await db.Collections.Where(c => colIds.Contains(c.Id)).ToListAsync(ct);
        var tags = await db.Tags.Where(t => tagIds.Contains(t.Id)).ToListAsync(ct);
        p.Collections.Clear(); foreach (var c in cols) p.Collections.Add(c);
        p.Tags.Clear(); foreach (var t in tags) p.Tags.Add(t);
        if (added && tags.Any(t => t.Name.Contains("Bestseller", StringComparison.OrdinalIgnoreCase))) p.IsFeatured = true;
        if (added)
        {
            var names = (item.Collections ?? []).Select(c => c.Name.Trim()).ToList();
            var home = names.Any(n => options.HomeCityCollections.Contains(n, StringComparer.OrdinalIgnoreCase));
            var otherOnly = !home && names.Any(n => options.OtherCityCollections.Contains(n, StringComparer.OrdinalIgnoreCase));
            if (otherOnly) p.IsActive = false;
        }

        var category = await db.Categories.FindAsync([p.CategoryId], ct);
        PricingService.Apply(p, category, settings);
        return (added, priceChanged);
    }

    private void SyncImages(Product p, List<DsImage> images, ProductVariant? variant)
    {
        var order = p.Images.Count(i => i.VariantId == null);
        foreach (var img in images.Where(i => i.Status == 1))
        {
            var existing = p.Images.FirstOrDefault(i => i.VendorImageId == img.ImageId);
            if (existing is not null) continue;
            // Variant images often repeat product images under a new id — skip identical URLs.
            if (p.Images.Any(i => i.Url == img.BestUrl())) continue;
            // The vendor sometimes stores YouTube links as "images" — keep them as embeddable videos.
            // (sometimes the link is garbled and the real one is only in the thumbnail field)
            var youTubeId = ImageOptimizer.YouTubeId(img.ImageUrl) ?? ImageOptimizer.YouTubeId(img.ThumbnailUrl);
            p.Images.Add(new ProductImage
            {
                VendorImageId = img.ImageId,
                Url = youTubeId is null ? img.BestUrl() : $"https://www.youtube.com/watch?v={youTubeId}",
                ThumbnailUrl = youTubeId is null ? img.ThumbnailUrl : $"https://img.youtube.com/vi/{youTubeId}/hqdefault.jpg",
                AltText = img.AltText,
                MediaType = img.MediaType == 3 || youTubeId is not null ? MediaType.Video : MediaType.Image,
                SortOrder = variant is null ? order++ : 100 + variant.SortOrder,
                VariantId = variant?.Id is > 0 ? variant.Id : null
            });
        }

    }

    private static void SyncOptionsAndVariants(Product p, DsItem item)
    {
        // Options (Size, Colour …). Names and colour codes are stored exactly as the vendor sends them (VendorName /
        // VendorValue / VendorColorCode); what customers see follows the vendor unless it was changed in Admin.
        var groups = (item.ItemOptionVariants ?? []).SelectMany(g => g).ToList();
        var colourOptions = groups.Count(g => ProductOptions.IsColourName(g.Key.Trim()));
        var pos = 0;
        foreach (var (optionName, values) in groups)
        {
            var vendorName = optionName.Trim();
            var opt = p.Options.FirstOrDefault(o => (o.VendorName ?? o.Name).Equals(vendorName, StringComparison.OrdinalIgnoreCase));
            if (opt is null) { opt = new ProductOption { Name = vendorName }; p.Options.Add(opt); }
            opt.VendorName = vendorName;
            if (!opt.IsCustomized)
            {
                opt.Position = pos;
                opt.IsColour = ProductOptions.IsColourName(vendorName);
                opt.Name = ProductOptions.DefaultName(vendorName, colourOptions);
            }
            pos++;
            foreach (var v in values)
            {
                var val = opt.Values.FirstOrDefault(x => x.VendorValueId == v.VariantNameId);
                if (val is null)
                {
                    val = new ProductOptionValue { VendorValueId = v.VariantNameId, SortOrder = opt.Values.Count == 0 ? 0 : opt.Values.Max(x => x.SortOrder) + 1 };
                    opt.Values.Add(val);
                }
                val.VendorValue = v.VariantName.Trim();
                val.VendorColorCode = string.IsNullOrWhiteSpace(v.ColourCode) ? null : v.ColourCode.Trim();
                if (!val.IsCustomized)
                {
                    val.Value = val.VendorValue;
                    val.ColorCode = val.VendorColorCode;
                }
            }
        }

        var allValues = p.Options.SelectMany(o => o.Values).ToList();
        var seen = new HashSet<long>();
        var sort = 0;
        foreach (var dv in item.Variants ?? [])
        {
            seen.Add(dv.VariantId);
            var v = p.Variants.FirstOrDefault(x => x.VendorVariantId == dv.VariantId);
            if (v is null) { v = new ProductVariant { VendorVariantId = dv.VariantId }; p.Variants.Add(v); }
            v.VendorName = dv.VariantName.Trim();
            if (string.IsNullOrEmpty(v.Name)) v.Name = v.VendorName;
            v.Sku = string.IsNullOrWhiteSpace(dv.SkuId) ? v.Sku : dv.SkuId;
            v.VendorMrp = dv.Price;
            v.VendorPrice = dv.DiscountedPrice > 0 ? dv.DiscountedPrice : dv.Price;
            v.IsAvailable = dv.Available == 1;
            v.SortOrder = sort++;

            // Vendor values follow the vendor; values added in Admin (e.g. a new option's first value) stay.
            var wanted = (dv.OptionVariantNames ?? []).Select(o => o.VariantNameId).ToHashSet();
            foreach (var ov in v.OptionValues.Where(x => x.VendorValueId is not null && !wanted.Contains(x.VendorValueId.Value)).ToList())
                v.OptionValues.Remove(ov);
            foreach (var val in allValues.Where(x => x.VendorValueId is not null && wanted.Contains(x.VendorValueId.Value)))
                if (!v.OptionValues.Contains(val)) v.OptionValues.Add(val);
            // A value removed in Admin keeps its variants off.
            v.IsActive = dv.Status == 1 && !v.OptionValues.Any(o => o.IsHidden);
        }
        // Variants removed at the vendor are deactivated (never deleted — orders may reference them).
        foreach (var v in p.Variants.Where(v => v.VendorVariantId is not null && !seen.Contains(v.VendorVariantId.Value)))
            v.IsActive = false;

        // Variants added in Admin (a new colour, …) copy price and stock from the vendor variant they were made from.
        foreach (var c in p.Variants.Where(v => v.VendorVariantId is null && v.SourceVariantId is not null))
        {
            var src = p.Variants.FirstOrDefault(x => x.Id == c.SourceVariantId);
            if (src is null || c.OptionValues.Count == 0) continue;
            c.VendorMrp = src.VendorMrp;
            c.VendorPrice = src.VendorPrice;
            c.IsAvailable = src.IsAvailable;
            c.IsActive = src.IsActive && !c.OptionValues.Any(o => o.IsHidden);
        }
        ProductOptions.RefreshVariantNames(p);
    }

    /// <summary>Pulls dimensions / material / warranty lines out of the vendor's free-text description.</summary>
    private static void ExtractAttributes(Product p)
    {
        var text = string.Join('\n', p.Description ?? "", HtmlTag().Replace(p.DescriptionHtml ?? "", "\n"));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => LeadingSymbols().Replace(l, "").Trim()).Where(l => l.Length > 3).ToList();

        static string Clip(string s) => s.Length > 200 ? s[..200] : s;
        p.Warranty ??= lines.FirstOrDefault(l => l.Contains("warranty", StringComparison.OrdinalIgnoreCase)) is { } w ? Clip(w) : null;
        p.Dimensions ??= lines.FirstOrDefault(l => DimensionPattern().IsMatch(l)) is { } d ? Clip(d) : null;
        p.Material ??= lines.FirstOrDefault(l => MaterialPattern().IsMatch(l)) is { } m ? Clip(m) : null;
    }

    private const string Cities = "Ahmedabad|Surat|Vadodara|Baroda|Bangalore|Banglore|Bengaluru|Hyderabad|Navi Mumbai|Mumbai|Pune|Kanpur|Unnao|Delhi|Chennai|Gujarat|India";

    /// <summary>Removes the vendor's SEO city phrases, e.g. "2 Door Wardrobe In Bangalore, Hyderabad And Pune" → "2 Door Wardrobe".</summary>
    public static string CleanName(string name)
    {
        var s = CityPhrase().Replace(name, "");
        s = TrailingWords().Replace(s, "");
        s = MultiSpace().Replace(s, " ").Trim(' ', ',', '-', '|');
        return s.Length < 4 ? name.Trim() : s;
    }

    [GeneratedRegex(@"\s*(?:-|,|\|)?\s*\b(?:manufacturers?\s+)?(?:in|at|for|from)\s+(?:(?:" + Cities + @")\b[\s,&]*(?:and\s+)?)+", RegexOptions.IgnoreCase)]
    private static partial Regex CityPhrase();
    [GeneratedRegex(@"\s+(?:manufacturers?|online|price)\s*$", RegexOptions.IgnoreCase)] private static partial Regex TrailingWords();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex MultiSpace();
    [GeneratedRegex("<[^>]+>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"^[^\p{L}\p{N}]+")] private static partial Regex LeadingSymbols();
    [GeneratedRegex(@"(dimension|size|height|width|depth|\d+\s*(""|inch|in|ft|cm|mm)\s*[x×*])", RegexOptions.IgnoreCase)] private static partial Regex DimensionPattern();
    [GeneratedRegex(@"(engineered wood|plywood|sheesham|teak|solid wood|mdf|particle board|metal|steel|fabric|leather|rexine|marble|glass|wpc|foam)", RegexOptions.IgnoreCase)] private static partial Regex MaterialPattern();

    /// <summary>Downloads vendor images to local storage so the storefront does not depend on the vendor CDN.</summary>
    public async Task<int> DownloadImagesAsync(int vendorId, CancellationToken ct)
    {
        progress.Stage = "Downloading images";
        var pending = await db.ProductImages
            .Where(i => i.LocalPath == null && i.MediaType == MediaType.Image && i.Product!.VendorId == vendorId)
            .ToListAsync(ct);
        progress.Total = pending.Count;
        progress.Processed = 0;

        var http = httpFactory.CreateClient("media");
        var count = 0;
        var downloaded = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var gate = new SemaphoreSlim(6);
        await Task.WhenAll(pending.Select(async img =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var name = $"/media/products/{img.ProductId}/{img.VendorImageId ?? img.Id}";
                var main = await SaveImageAsync(http, img.Url, name, ImageOptimizer.MaxMainSize, ct);
                if (main is null) return;
                img.LocalPath = main;
                if (!string.IsNullOrEmpty(img.ThumbnailUrl))
                    img.LocalThumbnailPath = await SaveImageAsync(http, img.ThumbnailUrl, name + "_t", ImageOptimizer.MaxThumbSize, ct);
                Interlocked.Increment(ref count);
                downloaded.Add(img.Id);
            }
            catch (Exception ex) { logger.LogWarning("Image download failed {Url}: {Msg}", img.Url, ex.Message); }
            finally
            {
                gate.Release();
                lock (progress) progress.Processed++;
            }
        }));
        await db.SaveChangesAsync(ct);

        // New photos: remove vendor marks / tidy them in the background (Admin → Image clean-up).
        if (await db.BusinessSettings.AsNoTracking().Select(s => s.ImageCleanAuto).FirstAsync(ct))
            cleaningQueue.Enqueue(downloaded);
        return count;
    }

    /// <summary>Downloads an image, verifies it really is an image, and stores an optimised copy. Returns the public path.</summary>
    private async Task<string?> SaveImageAsync(HttpClient http, string url, string pathWithoutExt, int maxSize, CancellationToken ct)
    {
        var data = await http.GetByteArrayAsync(url, ct);
        if (!ImageOptimizer.IsImage(data))
        {
            logger.LogWarning("Skipped non-image media {Url}", url);
            return null;
        }
        var optimised = ImageOptimizer.Optimize(data, maxSize);
        var ext = optimised is null ? Path.GetExtension(new Uri(url).AbsolutePath) : ".webp";
        if (string.IsNullOrEmpty(ext)) ext = ".webp";
        var path = pathWithoutExt + ext;
        using var ms = new MemoryStream(optimised ?? data);
        await storage.WriteAsync(ms, path, ct);
        return path;
    }

    /// <summary>One-off maintenance: shrinks previously downloaded oversized images and fixes YouTube links stored as images.</summary>
    public async Task<(int optimised, int videosFixed, long bytesSaved)> OptimizeExistingAsync(CancellationToken ct)
    {
        progress.Stage = "Optimising images";
        var images = await db.ProductImages.Where(i => i.LocalPath != null || i.Url.Contains("youtu")).ToListAsync(ct);
        progress.Total = images.Count;
        progress.Processed = 0;
        int optimised = 0, videos = 0;
        long saved = 0;
        foreach (var img in images)
        {
            progress.Processed++;
            var yt = ImageOptimizer.YouTubeId(img.Url) ?? ImageOptimizer.YouTubeId(img.ThumbnailUrl);
            if (yt is not null)
            {
                storage.Delete(img.LocalPath);
                storage.Delete(img.LocalThumbnailPath);
                img.LocalPath = img.LocalThumbnailPath = null;
                img.MediaType = MediaType.Video;
                img.Url = $"https://www.youtube.com/watch?v={yt}";
                img.ThumbnailUrl = $"https://img.youtube.com/vi/{yt}/hqdefault.jpg";
                videos++;
                continue;
            }
            foreach (var (getPath, setPath, max) in new (Func<string?>, Action<string>, int)[]
                     {
                         (() => img.LocalPath, v => img.LocalPath = v, ImageOptimizer.MaxMainSize),
                         (() => img.LocalThumbnailPath, v => img.LocalThumbnailPath = v, ImageOptimizer.MaxThumbSize)
                     })
            {
                var rel = getPath();
                if (rel is null) continue;
                var full = storage.FullPath(rel);
                if (!File.Exists(full)) continue;
                var data = await File.ReadAllBytesAsync(full, ct);
                if (!ImageOptimizer.IsImage(data)) { File.Delete(full); setPath(null!); continue; }
                var smaller = ImageOptimizer.Optimize(data, max);
                if (smaller is null) continue;
                var newRel = Path.ChangeExtension(rel, ".webp")!.Replace('\\', '/');
                if (newRel == rel) newRel = rel[..^5] + "_o.webp";
                await File.WriteAllBytesAsync(storage.FullPath(newRel), smaller, ct);
                if (newRel != rel) File.Delete(full);
                setPath(newRel);
                saved += data.Length - smaller.Length;
                optimised++;
            }
        }
        await db.SaveChangesAsync(ct);
        return (optimised, videos, saved);
    }
}
