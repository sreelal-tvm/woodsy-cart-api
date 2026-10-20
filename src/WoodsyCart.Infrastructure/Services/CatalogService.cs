using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Contracts;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

public class CatalogService(AppDbContext db, IMemoryCache cache)
{
    public const string CategoriesCacheKey = "catalog:categories";

    private static readonly string[] SizeOptionNames = ["size", "seater", "dimension"];

    /// <summary>Products visible on the storefront.</summary>
    public IQueryable<Product> Visible() =>
        db.Products.AsNoTracking().Where(p => p.IsActive && p.Category!.IsActive && p.SellingPrice > 0);

    public static int Discount(decimal mrp, decimal price) =>
        mrp > price && mrp > 0 ? (int)Math.Round((mrp - price) / mrp * 100) : 0;

    public static string? MediaUrl(ProductImage? i) => i is null ? null : i.LocalPath ?? i.Url;
    public static string? ThumbUrl(ProductImage? i) => i is null ? null : i.LocalThumbnailPath ?? i.ThumbnailUrl ?? i.LocalPath ?? i.Url;

    public static IQueryable<ProductCardDto> ToCards(IQueryable<Product> q) => q.Select(p => new ProductCardDto(
        p.Id, p.Name, p.Slug,
        p.Images.Where(i => i.MediaType == Domain.MediaType.Image).OrderBy(i => i.SortOrder).Select(i => i.LocalPath ?? i.Url).FirstOrDefault(),
        p.Images.Where(i => i.MediaType == Domain.MediaType.Image).OrderBy(i => i.SortOrder).Select(i => i.LocalThumbnailPath ?? i.ThumbnailUrl ?? i.LocalPath ?? i.Url).FirstOrDefault(),
        p.Mrp, p.OfferPrice ?? p.SellingPrice,
        p.Mrp > (p.OfferPrice ?? p.SellingPrice) && p.Mrp > 0 ? (int)Math.Round((p.Mrp - (p.OfferPrice ?? p.SellingPrice)) / p.Mrp * 100) : 0,
        p.RatingAverage, p.RatingCount, p.IsAvailable,
        p.Category!.Name, p.Category.Slug,
        p.Tags.Select(t => t.Name).ToList(),
        p.Variants.Any(v => v.IsActive),
        p.Options.Where(o => o.IsColour).SelectMany(o => o.Values).Where(v => !v.IsHidden).OrderBy(v => v.SortOrder)
            .Select(v => v.ColorCode ?? v.Value).Take(5).ToList(),
        p.SellingPrice,
        p.OfferPrice != null ? p.Offer!.Name : null, p.OfferPrice != null ? p.Offer!.Slug : null, p.OfferPrice != null ? p.Offer!.ThemeColor : null));

    public async Task<List<CategoryDto>> GetCategoriesAsync() =>
        (await cache.GetOrCreateAsync(CategoriesCacheKey, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var cats = await db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder)
                .Select(c => new { c.Id, c.Name, c.Slug, c.ImageUrl, c.Description }).ToListAsync();
            var products = await Visible().Select(p => new { p.Id, p.CategoryId, p.IsFeatured }).ToListAsync();
            var counts = products.GroupBy(p => p.CategoryId).ToDictionary(g => g.Key, g => g.Count());

            // Categories without their own image use the first image of their best product.
            var coverProduct = products.GroupBy(p => p.CategoryId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.IsFeatured).ThenBy(p => p.Id).First().Id);
            var coverIds = coverProduct.Values.ToList();
            var images = (await db.ProductImages.AsNoTracking()
                    .Where(i => coverIds.Contains(i.ProductId) && i.MediaType == Domain.MediaType.Image)
                    .Select(i => new { i.ProductId, i.SortOrder, Url = i.LocalThumbnailPath ?? i.ThumbnailUrl ?? i.LocalPath ?? i.Url }).ToListAsync())
                .GroupBy(i => i.ProductId).ToDictionary(g => g.Key, g => g.OrderBy(i => i.SortOrder).First().Url);

            return cats.Where(c => counts.ContainsKey(c.Id))
                .Select(c => new CategoryDto(c.Id, c.Name, c.Slug,
                    c.ImageUrl ?? images.GetValueOrDefault(coverProduct.GetValueOrDefault(c.Id)), c.Description, counts[c.Id]))
                .ToList();
        }))!;

    private IQueryable<Product> Filter(ProductQuery q, bool includePriceAndOptions = true)
    {
        var query = Visible();
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var terms = q.Search.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var t in terms)
                query = query.Where(p => p.Name.Contains(t) || p.Category!.Name.Contains(t) || (p.Material != null && p.Material.Contains(t)));
        }
        if (!string.IsNullOrWhiteSpace(q.Category)) query = query.Where(p => p.Category!.Slug == q.Category);
        if (!string.IsNullOrWhiteSpace(q.Collection)) query = query.Where(p => p.Collections.Any(c => c.Slug == q.Collection));
        if (!string.IsNullOrWhiteSpace(q.Tag)) query = query.Where(p => p.Tags.Any(t => t.Name == q.Tag));
        if (!string.IsNullOrWhiteSpace(q.Offer)) query = query.Where(p => p.OfferPrice != null && p.Offer!.Slug == q.Offer);
        if (q.InStock == true) query = query.Where(p => p.IsAvailable);
        if (q.Featured == true) query = query.Where(p => p.IsFeatured);
        if (q.MinDiscount is > 0) query = query.Where(p => p.Mrp > 0 && (p.Mrp - (p.OfferPrice ?? p.SellingPrice)) * 100 / p.Mrp >= q.MinDiscount);
        if (!includePriceAndOptions) return query;

        if (q.MinPrice is not null) query = query.Where(p => (p.OfferPrice ?? p.SellingPrice) >= q.MinPrice);
        if (q.MaxPrice is not null) query = query.Where(p => (p.OfferPrice ?? p.SellingPrice) <= q.MaxPrice);
        if (!string.IsNullOrWhiteSpace(q.Colors))
        {
            var colors = q.Colors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(p => p.Options.Any(o => o.IsColour && o.Values.Any(v => !v.IsHidden && colors.Contains(v.Value))));
        }
        if (!string.IsNullOrWhiteSpace(q.Sizes))
        {
            var sizes = q.Sizes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            query = query.Where(p => p.Options.Any(o => o.Name == "Size" && o.Values.Any(v => sizes.Contains(v.Value))));
        }
        return query;
    }

    public async Task<PagedResult<ProductCardDto>> SearchAsync(ProductQuery q)
    {
        q.PageSize = Math.Clamp(q.PageSize, 1, 60);
        q.Page = Math.Max(1, q.Page);
        var query = Filter(q);
        var total = await query.CountAsync();

        query = q.Sort switch
        {
            "newest" => query.OrderByDescending(p => p.CreatedAt),
            "price_asc" => query.OrderBy(p => p.OfferPrice ?? p.SellingPrice),
            "price_desc" => query.OrderByDescending(p => p.OfferPrice ?? p.SellingPrice),
            "discount" => query.OrderByDescending(p => p.Mrp > 0 ? (p.Mrp - (p.OfferPrice ?? p.SellingPrice)) / p.Mrp : 0),
            "rating" => query.OrderByDescending(p => p.RatingAverage).ThenByDescending(p => p.RatingCount),
            _ => query.OrderByDescending(p => p.IsAvailable).ThenByDescending(p => p.IsFeatured).ThenByDescending(p => p.SoldCount).ThenBy(p => p.SortOrder).ThenByDescending(p => p.Id)
        };

        var items = await ToCards(query.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize)).ToListAsync();
        return new PagedResult<ProductCardDto> { Items = items, Total = total, Page = q.Page, PageSize = q.PageSize };
    }

    public async Task<FacetsDto> GetFacetsAsync(ProductQuery q)
    {
        var baseQuery = Filter(q, includePriceAndOptions: false);
        var prices = await baseQuery.GroupBy(_ => 1).Select(g => new { Min = g.Min(p => p.OfferPrice ?? p.SellingPrice), Max = g.Max(p => p.OfferPrice ?? p.SellingPrice) }).FirstOrDefaultAsync();

        var catQuery = Filter(new ProductQuery { Search = q.Search, Collection = q.Collection, Tag = q.Tag }, false);
        var categories = await catQuery.GroupBy(p => new { p.Category!.Slug, p.Category.Name, p.Category.SortOrder })
            .Select(g => new { g.Key.Slug, g.Key.Name, g.Key.SortOrder, Count = g.Count() })
            .OrderBy(x => x.SortOrder).ToListAsync();

        var productIds = await baseQuery.Select(p => p.Id).ToListAsync();
        var optionValues = await db.ProductOptionValues.AsNoTracking()
            .Where(v => productIds.Contains(v.Option!.ProductId) && !v.IsHidden)
            .Select(v => new { Option = v.Option!.Name, v.Option.IsColour, v.Value, v.ColorCode, Id = v.Option.ProductId }).ToListAsync();
        var colors = optionValues.Where(v => v.IsColour)
            .GroupBy(v => v.Value.Trim()).Select(g => new FacetItem(g.Key, g.Key, g.Select(x => x.Id).Distinct().Count(), g.First().ColorCode))
            .OrderByDescending(f => f.Count).Take(30).ToList();
        var sizes = optionValues.Where(v => v.Option.Equals("Size", StringComparison.OrdinalIgnoreCase))
            .GroupBy(v => v.Value.Trim()).Select(g => new FacetItem(g.Key, g.Key, g.Select(x => x.Id).Distinct().Count()))
            .OrderByDescending(f => f.Count).Take(30).ToList();

        var collections = await db.Collections.AsNoTracking().Where(c => c.IsActive)
            .Select(c => new { c.Slug, c.Name, Count = c.Products.Count(p => productIds.Contains(p.Id)) })
            .Where(c => c.Count > 0).ToListAsync();

        return new FacetsDto(prices?.Min ?? 0, prices?.Max ?? 0,
            categories.Select(c => new FacetItem(c.Slug, c.Name, c.Count)).ToList(),
            colors, sizes,
            collections.Select(c => new FacetItem(c.Slug, c.Name, c.Count)).ToList());
    }

    public async Task<ProductDetailDto?> GetProductAsync(string slugOrId)
    {
        var q = Visible()
            .Include(p => p.Category)
            .Include(p => p.Images)
            .Include(p => p.Options).ThenInclude(o => o.Values)
            .Include(p => p.Variants).ThenInclude(v => v.OptionValues)
            .Include(p => p.Tags).Include(p => p.Collections).Include(p => p.Offer)
            .AsSplitQuery();
        var p = int.TryParse(slugOrId, out var id)
            ? await q.FirstOrDefaultAsync(x => x.Id == id)
            : await q.FirstOrDefaultAsync(x => x.Slug == slugOrId);
        if (p is null) return null;

        var c = p.Category!;
        return new ProductDetailDto(
            p.Id, p.Name, p.Slug, p.Description, p.DescriptionHtml,
            p.Mrp, p.OfferPrice ?? p.SellingPrice, Discount(p.Mrp, p.OfferPrice ?? p.SellingPrice), p.IsAvailable,
            p.RatingAverage, p.RatingCount,
            new CategoryDto(c.Id, c.Name, c.Slug, c.ImageUrl, null, 0),
            p.Brand, p.Sku, p.HsnCode, p.GstRate, p.Material, p.Dimensions, p.Warranty, p.DeliveryDays, p.AssemblyIncluded,
            p.Images.OrderBy(i => i.SortOrder).Select(i => new ImageDto(i.Id, MediaUrl(i)!, ThumbUrl(i), i.AltText ?? p.Name, (int)i.MediaType, i.VariantId)).ToList(),
            p.Options.OrderBy(o => o.Position).Select(o => new OptionDto(o.Id, o.Name,
                o.Values.Where(v => !v.IsHidden && (p.Variants.Any(x => x.IsActive && x.OptionValues.Any(ov => ov.Id == v.Id)) || p.Variants.Count == 0))
                    .OrderBy(v => v.SortOrder).ThenBy(v => v.Id)
                    .Select(v => new OptionValueDto(v.Id, v.Value, v.ColorCode)).ToList(), o.IsColour)).Where(o => o.Values.Count > 0).ToList(),
            p.Variants.Where(v => v.IsActive && v.SellingPrice > 0).OrderBy(v => v.SortOrder)
                .Select(v => new VariantDto(v.Id, v.Name, v.Mrp, v.OfferPrice ?? v.SellingPrice, v.IsAvailable && p.IsAvailable, v.OptionValues.Select(o => o.Id).ToList(), v.SellingPrice)).ToList(),
            p.Tags.Select(t => t.Name).ToList(),
            p.Collections.Where(x => x.IsActive).Select(x => new CollectionDto(x.Id, x.Name, x.Slug, x.ImageUrl, 0)).ToList(),
            p.MetaTitle, p.MetaDescription,
            p.SellingPrice,
            p.OfferPrice is not null && p.Offer is not null
                ? new ProductOfferDto(p.Offer.Name, p.Offer.Slug, p.Offer.Tagline, p.Offer.ThemeColor, p.Offer.EndsAt, p.Offer.CouponCode)
                : null);
    }

    public async Task<List<ProductCardDto>> GetRelatedAsync(int productId, int take = 8)
    {
        var p = await db.Products.AsNoTracking().Where(x => x.Id == productId).Select(x => new { x.CategoryId, x.SellingPrice }).FirstOrDefaultAsync();
        if (p is null) return [];
        var q = Visible().Where(x => x.CategoryId == p.CategoryId && x.Id != productId && x.IsAvailable)
            .OrderBy(x => Math.Abs(x.SellingPrice - p.SellingPrice));
        return await ToCards(q.Take(take)).ToListAsync();
    }

    public async Task<HomeDto> GetHomeAsync()
    {
        var banners = await db.Banners.AsNoTracking().Where(b => b.IsActive).OrderBy(b => b.SortOrder)
            .Select(b => new BannerDto(b.Id, b.Title, b.Subtitle, b.ImageUrl, b.LinkUrl, b.ButtonText)).ToListAsync();
        var categories = (await GetCategoriesAsync()).Take(20).ToList();
        var available = Visible().Where(p => p.IsAvailable);
        var featured = await ToCards(available.Where(p => p.IsFeatured).OrderByDescending(p => p.SoldCount).ThenByDescending(p => p.Id).Take(12)).ToListAsync();
        var newest = await ToCards(available.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Take(12)).ToListAsync();
        var deals = await ToCards(available.Where(p => p.Mrp > (p.OfferPrice ?? p.SellingPrice))
            .OrderByDescending(p => (p.Mrp - (p.OfferPrice ?? p.SellingPrice)) / p.Mrp).Take(12)).ToListAsync();
        var collections = await db.Collections.AsNoTracking().Where(c => c.IsActive && c.ImageUrl != null).OrderBy(c => c.SortOrder)
            .Select(c => new CollectionDto(c.Id, c.Name, c.Slug, c.ImageUrl, c.Products.Count(p => p.IsActive))).ToListAsync();
        var offers = new List<OfferDto>();
        foreach (var o in await LiveOffersQuery().Where(o => o.ShowOnHome).ToListAsync())
            offers.Add(await ToOfferDtoAsync(o, 8));
        return new HomeDto(banners, categories, featured, newest, deals, collections, offers.Where(o => o.ProductCount > 0).ToList());
    }

    // ---------- Festival offers ----------

    private IQueryable<Offer> LiveOffersQuery()
    {
        var now = DateTime.UtcNow;
        return db.Offers.AsNoTracking().Where(o => o.IsActive && o.StartsAt <= now && o.EndsAt > now).OrderBy(o => o.SortOrder).ThenBy(o => o.EndsAt);
    }

    public static string OfferStatus(Offer o, DateTime now) =>
        !o.IsActive ? "off" : o.StartsAt > now ? "upcoming" : o.EndsAt <= now ? "ended" : "live";

    private async Task<OfferDto> ToOfferDtoAsync(Offer o, int take)
    {
        var products = Visible().Where(p => p.OfferId == o.Id && p.OfferPrice != null);
        var count = await products.CountAsync();
        var cards = take == 0 ? [] : await ToCards(products.OrderByDescending(p => p.IsAvailable).ThenByDescending(p => p.IsFeatured)
            .ThenByDescending(p => p.SoldCount).ThenByDescending(p => p.Id).Take(take)).ToListAsync();
        return new OfferDto(o.Id, o.Name, o.Slug, o.Tagline, o.Description, o.BannerImageUrl, o.ThemeColor, o.StartsAt, o.EndsAt,
            OfferStatus(o, DateTime.UtcNow), (int)o.DiscountType, o.Value, o.MaxDiscount, o.CouponCode, count, cards);
    }

    /// <summary>Offers running now (for the Offers page and header link).</summary>
    public async Task<List<OfferDto>> GetLiveOffersAsync()
    {
        var list = new List<OfferDto>();
        foreach (var o in await LiveOffersQuery().ToListAsync()) list.Add(await ToOfferDtoAsync(o, 4));
        return list.Where(o => o.ProductCount > 0).ToList();
    }

    /// <summary>One offer's page. Upcoming and ended offers are returned without products so the page can say so.</summary>
    public async Task<OfferDto?> GetOfferAsync(string slug)
    {
        var o = await db.Offers.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == slug && x.IsActive);
        if (o is null) return null;
        return OfferStatus(o, DateTime.UtcNow) == "live"
            ? await ToOfferDtoAsync(o, 0)
            : new OfferDto(o.Id, o.Name, o.Slug, o.Tagline, o.Description, o.BannerImageUrl, o.ThemeColor, o.StartsAt, o.EndsAt,
                OfferStatus(o, DateTime.UtcNow), (int)o.DiscountType, o.Value, o.MaxDiscount, o.CouponCode, 0, []);
    }

    public async Task<PagedResult<ReviewDto>> GetReviewsAsync(int productId, int page)
    {
        var q = db.Reviews.AsNoTracking().Where(r => r.ProductId == productId && r.IsApproved);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * 10).Take(10)
            .Select(r => new ReviewDto(r.Id, r.User!.FullName, r.Rating, r.Title, r.Comment, r.IsVerifiedPurchase, r.CreatedAt)).ToListAsync();
        return new PagedResult<ReviewDto> { Items = items, Total = total, Page = page, PageSize = 10 };
    }

    public async Task AddReviewAsync(int productId, int userId, CreateReviewRequest req)
    {
        if (req.Rating is < 1 or > 5) throw new AppException("Rating must be between 1 and 5.");
        if (!await db.Products.AnyAsync(p => p.Id == productId)) throw new AppException("Product not found", 404);
        if (await db.Reviews.AnyAsync(r => r.ProductId == productId && r.UserId == userId)) throw new AppException("You have already reviewed this product.");
        var verified = await db.OrderItems.AnyAsync(i => i.ProductId == productId && i.Order!.UserId == userId && i.Order.Status == Domain.OrderStatus.Delivered);
        db.Reviews.Add(new Review
        {
            ProductId = productId, UserId = userId, Rating = req.Rating,
            Title = req.Title?.Trim(), Comment = req.Comment?.Trim(), IsVerifiedPurchase = verified
        });
        await db.SaveChangesAsync();
    }

    public static async Task RefreshRatingAsync(AppDbContext db, int productId)
    {
        var stats = await db.Reviews.Where(r => r.ProductId == productId && r.IsApproved)
            .GroupBy(_ => 1).Select(g => new { Avg = g.Average(r => (decimal)r.Rating), Count = g.Count() }).FirstOrDefaultAsync();
        var p = await db.Products.FindAsync(productId);
        if (p is null) return;
        p.RatingAverage = Math.Round(stats?.Avg ?? 0, 1);
        p.RatingCount = stats?.Count ?? 0;
        await db.SaveChangesAsync();
    }
}
