using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

/// <summary>Clears the storefront category cache after any successful change to the catalogue.</summary>
public class InvalidateCatalogCacheAttribute : ActionFilterAttribute
{
    public override void OnActionExecuted(ActionExecutedContext context)
    {
        if (context.Exception is null && !HttpMethods.IsGet(context.HttpContext.Request.Method))
            context.HttpContext.RequestServices.GetRequiredService<IMemoryCache>().Remove(CatalogService.CategoriesCacheKey);
    }
}

public record AdminProductRow(int Id, string Name, string Slug, string? ImageUrl, int CategoryId, string CategoryName,
    decimal VendorPrice, decimal GstRate, decimal VendorCost, decimal SellingPrice, decimal Mrp, decimal Margin, decimal MarginPercent, decimal? PreviousVendorPrice,
    bool IsActive, bool IsAvailable, bool IsFeatured, bool VendorPriceChanged, int VariantsCount, ProductSource Source,
    decimal? CommissionPercentOverride, int SoldCount, decimal RatingAverage, DateTime? LastSyncedAt, DateTime CreatedAt);

public class AdminProductQuery
{
    public string? Search { get; set; }
    public int? CategoryId { get; set; }
    public string? Status { get; set; } // published, draft, unavailable, price-changed, featured, low-margin
    public string Sort { get; set; } = "newest";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public record AdminVariantDto(int Id, string Name, string? Sku, decimal VendorMrp, decimal VendorPrice, decimal Mrp, decimal SellingPrice,
    bool IsAvailable, bool IsActive, List<string> Options, string? VendorName = null, bool IsCopy = false);
public record AdminOptionValueDto(int Id, string Value, string? ColorCode, string? VendorValue, string? VendorColorCode,
    bool FromVendor, bool IsCustomized, bool IsHidden, int SortOrder, int Variants);
public record AdminOptionDto(int Id, string Name, string? VendorName, bool IsColour, bool IsCustomized, int Position, bool FromVendor, List<AdminOptionValueDto> Values);
public record AdminImageDto(int Id, string Url, string? ThumbnailUrl, bool IsLocal, int MediaType, int SortOrder, int? VariantId);
public record AdminProductDetail(
    decimal VendorCost, bool VendorPricesIncludeGst,
    int Id, string Name, string Slug, string? Description, string? DescriptionHtml, int CategoryId, int? VendorId,
    string? Brand, string? Sku, string? HsnCode, decimal GstRate, string? Material, string? Dimensions, string? Warranty,
    int DeliveryDays, bool AssemblyIncluded, decimal VendorMrp, decimal VendorPrice, decimal? PreviousVendorPrice, bool VendorPriceChanged,
    decimal Mrp, decimal SellingPrice, decimal? CommissionPercentOverride, decimal EffectiveCommission, bool ManualPrice,
    bool IsActive, bool IsAvailable, bool IsFeatured, bool LockContent, ProductSource Source, long? VendorProductId, string? VendorUrl,
    DateTime? LastSyncedAt, string? MetaTitle, string? MetaDescription, List<int> CollectionIds, List<int> TagIds,
    List<AdminImageDto> Images, List<AdminVariantDto> Variants, int SoldCount, decimal RatingAverage, int RatingCount,
    List<AdminOptionDto>? Options = null);

public record VariantUpsert(int? Id, string Name, string? Sku, decimal VendorMrp, decimal VendorPrice, decimal Mrp, decimal SellingPrice, bool IsAvailable, bool IsActive);
public record ProductUpsert(
    string Name, string? Slug, string? Description, string? DescriptionHtml, int CategoryId, int? VendorId,
    string? Brand, string? Sku, string? HsnCode, decimal GstRate, string? Material, string? Dimensions, string? Warranty,
    int DeliveryDays, bool AssemblyIncluded, decimal VendorMrp, decimal VendorPrice, decimal Mrp, decimal SellingPrice,
    decimal? CommissionPercentOverride, bool ManualPrice, bool IsActive, bool IsAvailable, bool IsFeatured, bool LockContent,
    string? MetaTitle, string? MetaDescription, List<int>? CollectionIds, List<int>? TagIds, List<VariantUpsert>? Variants);

public record BulkProductAction(List<int> Ids, string Action, decimal? Value, int? CategoryId);
public record PriceCalcRequest(decimal VendorMrp, decimal VendorPrice, decimal? Commission, int? CategoryId, decimal? GstRate);

[InvalidateCatalogCache]
public class ProductsController(AppDbContext db, PricingService pricing, FileStorage storage,
    WoodsyCart.Infrastructure.Images.ImageCleaningQueue cleaningQueue) : AdminControllerBase
{
    [HttpGet]
    public async Task<PagedResult<AdminProductRow>> List([FromQuery] AdminProductQuery q)
    {
        // Our cost = vendor price + GST unless the vendor already quotes GST-inclusive prices.
        var incl = (await pricing.GetSettingsAsync()).VendorPricesIncludeGst;
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = long.TryParse(s, out var vid)
                ? query.Where(p => p.VendorProductId == vid || p.Id == vid || p.Name.Contains(s))
                : query.Where(p => p.Name.Contains(s) || (p.Sku != null && p.Sku.Contains(s)));
        }
        if (q.CategoryId is not null) query = query.Where(p => p.CategoryId == q.CategoryId);
        query = q.Status switch
        {
            "published" => query.Where(p => p.IsActive),
            "draft" => query.Where(p => !p.IsActive),
            "unavailable" => query.Where(p => !p.IsAvailable),
            "price-changed" => query.Where(p => p.VendorPriceChanged),
            "featured" => query.Where(p => p.IsFeatured),
            "low-margin" => query.Where(p => p.SellingPrice > 0 && (p.SellingPrice - (incl ? p.VendorPrice : p.VendorPrice * (100 + p.GstRate) / 100)) * 100 / p.SellingPrice < 8),
            _ => query
        };
        query = q.Sort switch
        {
            "name" => query.OrderBy(p => p.Name),
            "price" => query.OrderBy(p => p.SellingPrice),
            "price_desc" => query.OrderByDescending(p => p.SellingPrice),
            "margin" => query.OrderBy(p => p.SellingPrice - (incl ? p.VendorPrice : p.VendorPrice * (100 + p.GstRate) / 100)),
            "sold" => query.OrderByDescending(p => p.SoldCount),
            _ => query.OrderByDescending(p => p.Id)
        };
        var total = await query.CountAsync();
        var pageSize = Math.Clamp(q.PageSize, 1, 200);
        var page = await query.Skip((Math.Max(1, q.Page) - 1) * pageSize).Take(pageSize).Select(p => new
        {
            p.Id, p.Name, p.Slug,
            Image = p.Images.OrderBy(i => i.SortOrder).Select(i => i.LocalThumbnailPath ?? i.ThumbnailUrl ?? i.LocalPath ?? i.Url).FirstOrDefault(),
            p.CategoryId, CategoryName = p.Category!.Name, p.VendorPrice, p.GstRate, p.SellingPrice, p.Mrp,
            p.PreviousVendorPrice, p.IsActive, p.IsAvailable, p.IsFeatured, p.VendorPriceChanged, Variants = p.Variants.Count(v => v.IsActive), p.Source,
            p.CommissionPercentOverride, p.SoldCount, p.RatingAverage, p.LastSyncedAt, p.CreatedAt
        }).ToListAsync();
        var items = page.Select(p =>
        {
            var cost = incl ? p.VendorPrice : Math.Round(p.VendorPrice * (100 + p.GstRate) / 100, 2);
            var margin = p.SellingPrice - cost;
            return new AdminProductRow(p.Id, p.Name, p.Slug, p.Image, p.CategoryId, p.CategoryName, p.VendorPrice, p.GstRate, cost, p.SellingPrice, p.Mrp,
                margin, p.SellingPrice == 0 ? 0 : Math.Round(margin * 100 / p.SellingPrice, 1),
                p.PreviousVendorPrice, p.IsActive, p.IsAvailable, p.IsFeatured, p.VendorPriceChanged, p.Variants, p.Source,
                p.CommissionPercentOverride, p.SoldCount, p.RatingAverage, p.LastSyncedAt, p.CreatedAt);
        }).ToList();
        return new PagedResult<AdminProductRow> { Items = items, Total = total, Page = q.Page, PageSize = pageSize };
    }

    [HttpGet("{id:int}")]
    public async Task<AdminProductDetail> Get(int id)
    {
        var p = await db.Products.AsNoTracking().Include(x => x.Images).Include(x => x.Category)
            .Include(x => x.Variants).ThenInclude(v => v.OptionValues).ThenInclude(o => o.Option)
            .Include(x => x.Options).ThenInclude(o => o.Values)
            .Include(x => x.Collections).Include(x => x.Tags).AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Product not found", 404);
        var s = await pricing.GetSettingsAsync();
        return new AdminProductDetail(PricingService.CostOf(p.VendorPrice, p.GstRate, s), s.VendorPricesIncludeGst,
            p.Id, p.Name, p.Slug, p.Description, p.DescriptionHtml, p.CategoryId, p.VendorId,
            p.Brand, p.Sku, p.HsnCode, p.GstRate, p.Material, p.Dimensions, p.Warranty, p.DeliveryDays, p.AssemblyIncluded,
            p.VendorMrp, p.VendorPrice, p.PreviousVendorPrice, p.VendorPriceChanged, p.Mrp, p.SellingPrice,
            p.CommissionPercentOverride, PricingService.EffectiveCommission(p, p.Category, s), p.ManualPrice,
            p.IsActive, p.IsAvailable, p.IsFeatured, p.LockContent, p.Source, p.VendorProductId, p.VendorUrl, p.LastSyncedAt,
            p.MetaTitle, p.MetaDescription, p.Collections.Select(c => c.Id).ToList(), p.Tags.Select(t => t.Id).ToList(),
            p.Images.OrderBy(i => i.SortOrder).Select(i => new AdminImageDto(i.Id, i.LocalPath ?? i.Url, i.LocalThumbnailPath ?? i.ThumbnailUrl,
                i.LocalPath != null, (int)i.MediaType, i.SortOrder, i.VariantId)).ToList(),
            p.Variants.OrderBy(v => v.SortOrder).Select(v => new AdminVariantDto(v.Id, v.Name, v.Sku, v.VendorMrp, v.VendorPrice, v.Mrp, v.SellingPrice,
                v.IsAvailable, v.IsActive, v.OptionValues.OrderBy(o => o.Option!.Position).Select(o => $"{o.Option!.Name}: {o.Value}").ToList(),
                v.VendorVariantId is null ? null : v.VendorName, v.VendorVariantId is null && v.SourceVariantId is not null)).ToList(),
            p.SoldCount, p.RatingAverage, p.RatingCount,
            p.Options.OrderBy(o => o.Position).Select(o => new AdminOptionDto(o.Id, o.Name, o.VendorName, o.IsColour, o.IsCustomized, o.Position,
                o.Values.Any(v => v.VendorValueId != null),
                o.Values.OrderBy(v => v.SortOrder).ThenBy(v => v.Id).Select(v => new AdminOptionValueDto(v.Id, v.Value, v.ColorCode, v.VendorValue, v.VendorColorCode,
                    v.VendorValueId != null, v.IsCustomized, v.IsHidden, v.SortOrder,
                    p.Variants.Count(x => x.IsActive && x.OptionValues.Any(ov => ov.Id == v.Id)))).ToList())).ToList());
    }

    [HttpPost]
    public async Task<AdminProductDetail> Create(ProductUpsert req)
    {
        var p = new Product { Source = ProductSource.Manual };
        await Map(p, req);
        db.Products.Add(p);
        await db.SaveChangesAsync();
        return await Get(p.Id);
    }

    [HttpPut("{id:int}")]
    public async Task<AdminProductDetail> Update(int id, ProductUpsert req)
    {
        var p = await db.Products.Include(x => x.Variants).Include(x => x.Collections).Include(x => x.Tags)
            .FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Product not found", 404);
        await Map(p, req);
        await db.SaveChangesAsync();
        return await Get(id);
    }

    private async Task Map(Product p, ProductUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new AppException("Product name is required.");
        if (!await db.Categories.AnyAsync(c => c.Id == r.CategoryId)) throw new AppException("Choose a valid category.");
        var slug = Slug.Create(string.IsNullOrWhiteSpace(r.Slug) ? r.Name : r.Slug, 80);
        if (await db.Products.IgnoreQueryFilters().AnyAsync(x => x.Slug == slug && x.Id != p.Id)) slug = $"{slug}-{Random.Shared.Next(100, 999)}";

        p.Name = r.Name.Trim(); p.Slug = slug; p.Description = r.Description; p.DescriptionHtml = r.DescriptionHtml;
        p.CategoryId = r.CategoryId; p.VendorId = r.VendorId; p.Brand = r.Brand; p.Sku = r.Sku; p.HsnCode = r.HsnCode; p.GstRate = r.GstRate;
        p.Material = r.Material; p.Dimensions = r.Dimensions; p.Warranty = r.Warranty; p.DeliveryDays = r.DeliveryDays; p.AssemblyIncluded = r.AssemblyIncluded;
        p.VendorMrp = r.VendorMrp; p.VendorPrice = r.VendorPrice;
        p.CommissionPercentOverride = r.CommissionPercentOverride; p.ManualPrice = r.ManualPrice;
        if (r.ManualPrice) { p.Mrp = Math.Max(r.Mrp, r.SellingPrice); p.SellingPrice = r.SellingPrice; }
        p.IsActive = r.IsActive; p.IsAvailable = r.IsAvailable; p.IsFeatured = r.IsFeatured; p.LockContent = r.LockContent;
        p.MetaTitle = r.MetaTitle; p.MetaDescription = r.MetaDescription;

        if (r.CollectionIds is not null)
        {
            p.Collections.Clear();
            foreach (var c in await db.Collections.Where(c => r.CollectionIds.Contains(c.Id)).ToListAsync()) p.Collections.Add(c);
        }
        if (r.TagIds is not null)
        {
            p.Tags.Clear();
            foreach (var t in await db.Tags.Where(t => r.TagIds.Contains(t.Id)).ToListAsync()) p.Tags.Add(t);
        }
        if (r.Variants is not null)
        {
            foreach (var vr in r.Variants)
            {
                var v = vr.Id is > 0 ? p.Variants.FirstOrDefault(x => x.Id == vr.Id) : null;
                if (v is null) { v = new ProductVariant(); p.Variants.Add(v); }
                v.Name = vr.Name; v.Sku = vr.Sku; v.VendorMrp = vr.VendorMrp; v.VendorPrice = vr.VendorPrice;
                v.Mrp = vr.Mrp; v.SellingPrice = vr.SellingPrice; v.IsAvailable = vr.IsAvailable; v.IsActive = vr.IsActive;
            }
            var keep = r.Variants.Where(v => v.Id is > 0).Select(v => v.Id!.Value).ToHashSet();
            foreach (var removed in p.Variants.Where(v => v.Id > 0 && !keep.Contains(v.Id))) removed.IsActive = false;
        }

        var category = await db.Categories.FindAsync(p.CategoryId);
        PricingService.Apply(p, category, await pricing.GetSettingsAsync());
    }

    /// <summary>Saves the product's options (rename, colours, order, add, remove). The whole list is sent, in display order.</summary>
    [HttpPut("{id:int}/options")]
    public async Task<AdminProductDetail> SaveOptions(int id, List<OptionEdit> options, [FromServices] ProductOptionsService service)
    {
        await service.SaveAsync(id, options);
        return await Get(id);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await db.Products.FindAsync(id) ?? throw new AppException("Product not found", 404);
        p.IsDeleted = true;
        p.IsActive = false;
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> Bulk(BulkProductAction req)
    {
        var products = db.Products.Where(p => req.Ids.Contains(p.Id));
        switch (req.Action)
        {
            case "publish": await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, true)); break;
            case "unpublish": await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false)); break;
            case "feature": await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsFeatured, true)); break;
            case "unfeature": await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsFeatured, false)); break;
            case "delete": await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDeleted, true).SetProperty(p => p.IsActive, false)); break;
            case "ack-price": await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.VendorPriceChanged, false)); break;
            case "commission":
                await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.CommissionPercentOverride, req.Value).SetProperty(p => p.ManualPrice, false));
                await pricing.RecalculateAsync(productIds: req.Ids);
                break;
            case "gst" when req.Value is >= 0 and <= 28:
                await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.GstRate, req.Value!.Value));
                await pricing.RecalculateAsync(productIds: req.Ids);
                break;
            case "category" when req.CategoryId is not null:
                await products.ExecuteUpdateAsync(s => s.SetProperty(p => p.CategoryId, req.CategoryId.Value));
                await pricing.RecalculateAsync(productIds: req.Ids);
                break;
            default: throw new AppException("Unknown action.");
        }
        return NoContent();
    }

    [HttpPost("price-preview")]
    public async Task<object> PricePreview(PriceCalcRequest req)
    {
        var s = await pricing.GetSettingsAsync();
        var cat = req.CategoryId is null ? null : await db.Categories.FindAsync(req.CategoryId);
        var commission = req.Commission ?? cat?.CommissionPercent ?? s.DefaultCommissionPercent;
        var gstRate = req.GstRate ?? s.DefaultGstRate;
        var factor = PricingService.GstFactor(gstRate, s);
        var (mrp, selling) = PricingService.Calculate(req.VendorMrp, req.VendorPrice, commission, s.PriceRounding, factor);
        var cost = PricingService.CostOf(req.VendorPrice, gstRate, s);
        return new { commission, gstRate, includesGst = s.VendorPricesIncludeGst, cost, gstAmount = cost - req.VendorPrice, mrp, selling, margin = selling - cost };
    }

    [HttpPost("recalculate")]
    public async Task<object> Recalculate(int? categoryId) => new { updated = await pricing.RecalculateAsync(categoryId) };

    [HttpPost("{id:int}/images")]
    [RequestSizeLimit(20_000_000)]
    public async Task<List<AdminImageDto>> Upload(int id, List<IFormFile> files)
    {
        var p = await db.Products.Include(x => x.Images).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Product not found", 404);
        var order = p.Images.Count == 0 ? 0 : p.Images.Max(i => i.SortOrder) + 1;
        var added = new List<ProductImage>();
        foreach (var f in files)
        {
            await using var s = f.OpenReadStream();
            var url = await storage.SaveAsync(s, $"products/{id}", f.FileName);
            var img = new ProductImage { Url = url, LocalPath = url, SortOrder = order++, AltText = p.Name, MediaType = f.ContentType.StartsWith("video") ? MediaType.Video : MediaType.Image };
            p.Images.Add(img);
            added.Add(img);
        }
        await db.SaveChangesAsync();
        if (await db.BusinessSettings.AsNoTracking().Select(s => s.ImageCleanAuto).FirstAsync())
            cleaningQueue.Enqueue(added.Where(i => i.MediaType == MediaType.Image).Select(i => i.Id));
        return (await Get(id)).Images;
    }

    [HttpDelete("images/{imageId:int}")]
    public async Task<IActionResult> DeleteImage(int imageId)
    {
        var img = await db.ProductImages.FindAsync(imageId) ?? throw new AppException("Image not found", 404);
        storage.Delete(img.LocalPath);
        storage.Delete(img.LocalThumbnailPath);
        storage.Delete(img.OriginalPath);
        storage.Delete(img.OriginalThumbnailPath);
        db.ProductImages.Remove(img);
        await db.Products.Where(p => p.Id == img.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.LockContent, true));
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}/images/order")]
    public async Task<IActionResult> Reorder(int id, List<int> imageIds)
    {
        var images = await db.ProductImages.Where(i => i.ProductId == id).ToListAsync();
        for (var i = 0; i < imageIds.Count; i++)
        {
            var img = images.FirstOrDefault(x => x.Id == imageIds[i]);
            if (img is not null) img.SortOrder = i;
        }
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var rows = await db.Products.AsNoTracking().OrderBy(p => p.Category!.Name).ThenBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, Category = p.Category!.Name, p.VendorProductId, p.VendorMrp, p.VendorPrice, p.GstRate, p.Mrp, p.SellingPrice, p.IsActive, p.IsAvailable, p.VendorUrl })
            .ToListAsync();
        var settings = await pricing.GetSettingsAsync();
        var sb = new StringBuilder("Id,Name,Category,VendorItemId,VendorMrp,VendorPrice,GstRate,CostInclGst,Mrp,SellingPrice,Margin,Published,Available,VendorUrl\n");
        foreach (var r in rows)
        {
            var cost = PricingService.CostOf(r.VendorPrice, r.GstRate, settings);
            sb.AppendLine($"{r.Id},\"{r.Name.Replace("\"", "\"\"")}\",\"{r.Category}\",{r.VendorProductId},{r.VendorMrp},{r.VendorPrice},{r.GstRate},{cost},{r.Mrp},{r.SellingPrice},{r.SellingPrice - cost},{r.IsActive},{r.IsAvailable},{r.VendorUrl}");
        }
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "products.csv");
    }

    [HttpGet("lookups")]
    public async Task<object> Lookups() => new
    {
        categories = await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).Select(c => new { c.Id, c.Name }).ToListAsync(),
        collections = await db.Collections.AsNoTracking().OrderBy(c => c.SortOrder).Select(c => new { c.Id, c.Name }).ToListAsync(),
        tags = await db.Tags.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.Color }).ToListAsync(),
        vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.Name).Select(v => new { v.Id, v.Name }).ToListAsync()
    };
}

public record CategoryRow(int Id, string Name, string Slug, string? Description, string? ImageUrl, int SortOrder, bool IsActive, bool ShowOnHome,
    decimal? CommissionPercent, int ProductCount, int ActiveCount, long? VendorCategoryId);
public record CategoryUpsert(string Name, string? Slug, string? Description, string? ImageUrl, int SortOrder, bool IsActive, bool ShowOnHome, decimal? CommissionPercent);

[InvalidateCatalogCache]
public class CategoriesController(AppDbContext db, PricingService pricing) : AdminControllerBase
{
    [HttpGet]
    public Task<List<CategoryRow>> List() => db.Categories.AsNoTracking().OrderBy(c => c.SortOrder)
        .Select(c => new CategoryRow(c.Id, c.Name, c.Slug, c.Description, c.ImageUrl, c.SortOrder, c.IsActive, c.ShowOnHome, c.CommissionPercent,
            c.Products.Count(), c.Products.Count(p => p.IsActive), c.VendorCategoryId)).ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(CategoryUpsert r)
    {
        var c = new Category();
        await Map(c, r);
        db.Categories.Add(c);
        await db.SaveChangesAsync();
        return Ok(new { c.Id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CategoryUpsert r)
    {
        var c = await db.Categories.FindAsync(id) ?? throw new AppException("Category not found", 404);
        var commissionChanged = c.CommissionPercent != r.CommissionPercent;
        await Map(c, r);
        await db.SaveChangesAsync();
        if (commissionChanged) await pricing.RecalculateAsync(id);
        return NoContent();
    }

    private async Task Map(Category c, CategoryUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new AppException("Name is required.");
        var slug = Slug.Create(string.IsNullOrWhiteSpace(r.Slug) ? r.Name : r.Slug);
        if (await db.Categories.AnyAsync(x => x.Slug == slug && x.Id != c.Id)) throw new AppException("Another category already uses this URL slug.");
        c.Name = r.Name.Trim(); c.Slug = slug; c.Description = r.Description; c.ImageUrl = r.ImageUrl;
        c.SortOrder = r.SortOrder; c.IsActive = r.IsActive; c.ShowOnHome = r.ShowOnHome; c.CommissionPercent = r.CommissionPercent;
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (await db.Products.IgnoreQueryFilters().AnyAsync(p => p.CategoryId == id))
            throw new AppException("Move or delete this category's products first, or simply deactivate it.");
        await db.Categories.Where(c => c.Id == id).ExecuteDeleteAsync();
        return NoContent();
    }
}

public record CollectionRow(int Id, string Name, string Slug, string? ImageUrl, int SortOrder, bool IsActive, int ProductCount);
public record CollectionUpsert(string Name, string? ImageUrl, int SortOrder, bool IsActive);

[InvalidateCatalogCache]
public class CollectionsController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public Task<List<CollectionRow>> List() => db.Collections.AsNoTracking().OrderBy(c => c.SortOrder)
        .Select(c => new CollectionRow(c.Id, c.Name, c.Slug, c.ImageUrl, c.SortOrder, c.IsActive, c.Products.Count())).ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(CollectionUpsert r)
    {
        var slug = Slug.Create(r.Name);
        if (await db.Collections.AnyAsync(c => c.Slug == slug)) throw new AppException("A collection with this name exists.");
        db.Collections.Add(new Collection { Name = r.Name.Trim(), Slug = slug, ImageUrl = r.ImageUrl, SortOrder = r.SortOrder, IsActive = r.IsActive });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CollectionUpsert r)
    {
        var c = await db.Collections.FindAsync(id) ?? throw new AppException("Not found", 404);
        c.Name = r.Name.Trim(); c.ImageUrl = r.ImageUrl; c.SortOrder = r.SortOrder; c.IsActive = r.IsActive;
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record CustomerRow(int Id, string FullName, string Email, string? Phone, bool IsActive, bool IsGuest, int Orders, decimal TotalSpent, DateTime? LastOrderAt, DateTime CreatedAt, string? City);
public record CustomerDetail(int Id, string FullName, string Email, string? Phone, bool IsActive, bool IsGuest, string? Notes, DateTime CreatedAt, DateTime? LastLoginAt,
    int Orders, decimal TotalSpent, decimal AvgOrderValue, int WishlistCount, int CartCount,
    List<WoodsyCart.Infrastructure.Contracts.AddressDto> Addresses, List<WoodsyCart.Infrastructure.Contracts.OrderSummaryDto> RecentOrders, List<object> Reviews);
public record CustomerUpdate(bool IsActive, string? Notes);

public class CustomersController(AppDbContext db) : AdminControllerBase
{
    [HttpGet]
    public async Task<PagedResult<CustomerRow>> List(string? search, string sort = "newest", int page = 1, int pageSize = 25)
    {
        var q = db.Users.AsNoTracking().Where(u => u.Role == UserRole.Customer);
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(u => u.FullName.Contains(search) || u.Email.Contains(search) || (u.Phone != null && u.Phone.Contains(search)));
        // Sort on the entity (EF can't translate ordering by members of a projected record), then project.
        var ordered = sort switch
        {
            "spent" => q.OrderByDescending(u => u.Orders.Where(o => o.Status != OrderStatus.Cancelled).Sum(o => (decimal?)o.Total) ?? 0),
            "orders" => q.OrderByDescending(u => u.Orders.Count(o => o.Status != OrderStatus.Cancelled)),
            "recent" => q.OrderByDescending(u => u.Orders.Max(o => (DateTime?)o.CreatedAt)),
            _ => q.OrderByDescending(u => u.CreatedAt)
        };
        var total = await q.CountAsync();
        var items = await ordered.ThenByDescending(u => u.Id).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
            .Select(u => new CustomerRow(u.Id, u.FullName, u.Email, u.Phone, u.IsActive, u.IsGuest,
                u.Orders.Count(o => o.Status != OrderStatus.Cancelled),
                u.Orders.Where(o => o.Status != OrderStatus.Cancelled).Sum(o => (decimal?)o.Total) ?? 0,
                u.Orders.Max(o => (DateTime?)o.CreatedAt), u.CreatedAt,
                u.Addresses.OrderByDescending(a => a.IsDefault).Select(a => a.City).FirstOrDefault()))
            .ToListAsync();
        return new PagedResult<CustomerRow> { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    [HttpGet("{id:int}")]
    public async Task<CustomerDetail> Get(int id)
    {
        var u = await db.Users.AsNoTracking().Include(x => x.Addresses).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Customer not found", 404);
        var orders = db.Orders.AsNoTracking().Where(o => o.UserId == id);
        var valid = orders.Where(o => o.Status != OrderStatus.Cancelled);
        var count = await valid.CountAsync();
        var spent = await valid.SumAsync(o => (decimal?)o.Total) ?? 0;
        var recent = await orders.OrderByDescending(o => o.CreatedAt).Take(20)
            .Select(o => new WoodsyCart.Infrastructure.Contracts.OrderSummaryDto(o.Id, o.OrderNumber, o.CreatedAt, o.Status, o.PaymentStatus, o.PaymentMethod, o.Total,
                o.Items.Sum(i => i.Quantity), o.Items.Select(i => i.ImageUrl).FirstOrDefault(), o.Items.Select(i => i.ProductName).FirstOrDefault(),
                o.ExpectedDeliveryDate, o.DeliveryDateChangedAt)).ToListAsync();
        var reviews = await db.Reviews.AsNoTracking().Where(r => r.UserId == id).OrderByDescending(r => r.CreatedAt)
            .Select(r => (object)new { r.Id, r.Rating, r.Title, r.Comment, r.IsApproved, r.CreatedAt, Product = r.Product!.Name }).ToListAsync();
        return new CustomerDetail(u.Id, u.FullName, u.Email, u.Phone, u.IsActive, u.IsGuest, u.Notes, u.CreatedAt, u.LastLoginAt, count, spent,
            count == 0 ? 0 : Math.Round(spent / count, 2),
            await db.WishlistItems.CountAsync(w => w.UserId == id), await db.CartItems.CountAsync(c => c.UserId == id),
            u.Addresses.Select(a => new WoodsyCart.Infrastructure.Contracts.AddressDto(a.Id, a.Label, a.FullName, a.Phone, a.Line1, a.Line2, a.Landmark, a.City, a.State, a.Pincode, a.IsDefault)).ToList(),
            recent, reviews);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CustomerUpdate r)
    {
        var u = await db.Users.FindAsync(id) ?? throw new AppException("Customer not found", 404);
        u.IsActive = r.IsActive;
        u.Notes = r.Notes;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
