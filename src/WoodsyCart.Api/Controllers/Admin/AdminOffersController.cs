using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

public record OfferRow(int Id, string Name, string Slug, string? Tagline, string? BannerImageUrl, string ThemeColor,
    DateTime StartsAt, DateTime EndsAt, string Status, bool IsActive, bool ShowOnHome, int SortOrder,
    DiscountType DiscountType, decimal Value, decimal? MaxDiscount, OfferScope Scope, int ProductsOnOffer, OfferStats Stats);

public record OfferPickedProduct(int Id, string Name, string? ImageUrl, decimal Price);

public record OfferDetail(int Id, string Name, string Slug, string? Tagline, string? Description, string? BannerImageUrl, string ThemeColor,
    DateTime StartsAt, DateTime EndsAt, bool IsActive, bool ShowOnHome, int SortOrder,
    DiscountType DiscountType, decimal Value, decimal? MaxDiscount, decimal MinMarginPercent, OfferScope Scope, string? CouponCode,
    List<int> CategoryIds, List<OfferPickedProduct> Products, string Status, OfferStats Stats);

public record OfferUpsert(string Name, string? Slug, string? Tagline, string? Description, string? BannerImageUrl, string? ThemeColor,
    DateTime StartsAt, DateTime EndsAt, bool IsActive, bool ShowOnHome, int SortOrder,
    DiscountType DiscountType, decimal Value, decimal? MaxDiscount, decimal MinMarginPercent, OfferScope Scope, string? CouponCode,
    List<int>? CategoryIds, List<int>? ProductIds);

/// <summary>Festival offers (Diwali, Onam, …): time-limited discounts applied automatically to products.</summary>
public class OffersController(AppDbContext db, OfferService offers) : AdminControllerBase
{
    [HttpGet]
    public async Task<List<OfferRow>> List()
    {
        var now = DateTime.UtcNow;
        var list = await db.Offers.AsNoTracking().OrderByDescending(o => o.EndsAt > now).ThenBy(o => o.StartsAt).ToListAsync();
        var onOffer = await db.Products.AsNoTracking().Where(p => p.IsActive && p.OfferId != null && p.OfferPrice != null)
            .GroupBy(p => p.OfferId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var stats = await offers.StatsAsync();
        return list.Select(o => new OfferRow(o.Id, o.Name, o.Slug, o.Tagline, o.BannerImageUrl, o.ThemeColor, o.StartsAt, o.EndsAt,
            CatalogService.OfferStatus(o, now), o.IsActive, o.ShowOnHome, o.SortOrder, o.DiscountType, o.Value, o.MaxDiscount, o.Scope,
            onOffer.GetValueOrDefault(o.Id), stats.GetValueOrDefault(o.Id) ?? OfferStats.Empty)).ToList();
    }

    [HttpGet("{id:int}")]
    public async Task<OfferDetail> Get(int id)
    {
        var o = await db.Offers.AsNoTracking().Include(x => x.Categories).Include(x => x.Products).ThenInclude(p => p.Images)
                    .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Offer not found", 404);
        var stats = await offers.StatsAsync([id]);
        return new OfferDetail(o.Id, o.Name, o.Slug, o.Tagline, o.Description, o.BannerImageUrl, o.ThemeColor, o.StartsAt, o.EndsAt,
            o.IsActive, o.ShowOnHome, o.SortOrder, o.DiscountType, o.Value, o.MaxDiscount, o.MinMarginPercent, o.Scope, o.CouponCode,
            o.Categories.Select(c => c.Id).ToList(),
            o.Products.Select(p => new OfferPickedProduct(p.Id, p.Name,
                CatalogService.ThumbUrl(p.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).FirstOrDefault()), p.SellingPrice)).ToList(),
            CatalogService.OfferStatus(o, DateTime.UtcNow), stats.GetValueOrDefault(id) ?? OfferStats.Empty);
    }

    [HttpPost]
    public async Task<OfferApplyResult> Create(OfferUpsert r)
    {
        var o = new Offer();
        await MapAsync(o, r);
        db.Offers.Add(o);
        await db.SaveChangesAsync();
        return await offers.ApplyAsync();
    }

    [HttpPut("{id:int}")]
    public async Task<OfferApplyResult> Update(int id, OfferUpsert r)
    {
        var o = await db.Offers.Include(x => x.Categories).Include(x => x.Products).FirstOrDefaultAsync(x => x.Id == id)
                ?? throw new AppException("Offer not found", 404);
        await MapAsync(o, r);
        o.UpdatedAt = DateTime.UtcNow; // also when only the product list changed, so the price worker notices
        await db.SaveChangesAsync();
        return await offers.ApplyAsync();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (await db.OrderItems.AnyAsync(i => i.OfferId == id))
            throw new AppException("This offer has orders, so it can't be deleted (its sales report would be lost). Switch it off instead.");
        var o = await db.Offers.Include(x => x.Categories).Include(x => x.Products).FirstOrDefaultAsync(x => x.Id == id)
                ?? throw new AppException("Offer not found", 404);
        await db.Products.Where(p => p.OfferId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.OfferId, (int?)null));
        db.Offers.Remove(o);
        await db.SaveChangesAsync();
        await offers.ApplyAsync();
        return NoContent();
    }

    /// <summary>How many products the form's settings would reduce (nothing is saved).</summary>
    [HttpPost("preview")]
    public Task<OfferApplyResult> Preview(OfferUpsert r) =>
        offers.PreviewAsync(new Offer { DiscountType = r.DiscountType, Value = r.Value, MaxDiscount = r.MaxDiscount, MinMarginPercent = r.MinMarginPercent, Scope = r.Scope },
            r.CategoryIds ?? [], r.ProductIds ?? []);

    private async Task MapAsync(Offer o, OfferUpsert r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new AppException("Offer name is required.");
        if (r.EndsAt <= r.StartsAt) throw new AppException("The offer must end after it starts.");
        if (r.Value <= 0) throw new AppException("Enter the discount.");
        if (r.DiscountType == DiscountType.Percent && r.Value > 90) throw new AppException("A percentage discount can be at most 90%.");
        if (r.MinMarginPercent is < 0 or > 100) throw new AppException("Minimum margin must be between 0 and 100%.");
        if (r.Scope == OfferScope.Categories && r.CategoryIds is not { Count: > 0 }) throw new AppException("Choose at least one category.");
        if (r.Scope == OfferScope.Products && r.ProductIds is not { Count: > 0 }) throw new AppException("Choose at least one product.");

        var slug = Slug.Create(string.IsNullOrWhiteSpace(r.Slug) ? r.Name : r.Slug);
        if (await db.Offers.AnyAsync(x => x.Slug == slug && x.Id != o.Id)) throw new AppException("Another offer already uses this web address — change the name or URL slug.");

        o.Name = r.Name.Trim(); o.Slug = slug; o.Tagline = r.Tagline?.Trim(); o.Description = r.Description?.Trim();
        o.BannerImageUrl = string.IsNullOrWhiteSpace(r.BannerImageUrl) ? null : r.BannerImageUrl;
        o.ThemeColor = r.ThemeColor is { Length: 7 } c && c[0] == '#' ? c : "#e8590c";
        o.StartsAt = DateTime.SpecifyKind(r.StartsAt.ToUniversalTime(), DateTimeKind.Utc);
        o.EndsAt = DateTime.SpecifyKind(r.EndsAt.ToUniversalTime(), DateTimeKind.Utc);
        o.IsActive = r.IsActive; o.ShowOnHome = r.ShowOnHome; o.SortOrder = r.SortOrder;
        o.DiscountType = r.DiscountType; o.Value = r.Value; o.MaxDiscount = r.DiscountType == DiscountType.Percent ? r.MaxDiscount : null;
        o.MinMarginPercent = r.MinMarginPercent; o.Scope = r.Scope;
        o.CouponCode = string.IsNullOrWhiteSpace(r.CouponCode) ? null : r.CouponCode.Trim().ToUpperInvariant();

        o.Categories.Clear();
        if (r.Scope == OfferScope.Categories)
            foreach (var cat in await db.Categories.Where(x => r.CategoryIds!.Contains(x.Id)).ToListAsync()) o.Categories.Add(cat);
        o.Products.Clear();
        if (r.Scope == OfferScope.Products)
            foreach (var p in await db.Products.Where(x => r.ProductIds!.Contains(x.Id)).ToListAsync()) o.Products.Add(p);
    }
}
