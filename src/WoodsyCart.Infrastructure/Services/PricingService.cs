using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>
/// Converts vendor cost into our selling price.
/// Commission priority: product override → category → global default.
/// MRP shown to customers is never lower than the selling price, and never inflated above the vendor's MRP
/// unless our selling price itself exceeds it.
/// </summary>
public class PricingService(AppDbContext db)
{
    public static decimal Round(decimal price, PriceRounding rounding) => rounding switch
    {
        PriceRounding.Nearest10 => Math.Ceiling(price / 10m) * 10m,
        PriceRounding.Nearest100 => Math.Ceiling(price / 100m) * 100m,
        PriceRounding.EndWith99 => Math.Max(99, Math.Ceiling(price / 100m) * 100m - 1),
        _ => Math.Round(price, 0, MidpointRounding.AwayFromZero)
    };

    public static decimal EffectiveCommission(Product p, Category? c, BusinessSettings s) =>
        p.CommissionPercentOverride ?? c?.CommissionPercent ?? s.DefaultCommissionPercent;

    /// <summary>Multiplier that turns a vendor price into our real cost (adds GST when the vendor quotes prices before tax).</summary>
    public static decimal GstFactor(decimal gstRate, BusinessSettings s) =>
        s.VendorPricesIncludeGst ? 1m : 1m + gstRate / 100m;

    /// <summary>Our GST-inclusive cost for a vendor price.</summary>
    public static decimal CostOf(decimal vendorPrice, decimal gstRate, BusinessSettings s) =>
        Math.Round(vendorPrice * GstFactor(gstRate, s), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Selling price = round(vendor price × GST factor × (1 + commission%)).
    /// MRP = vendor MRP (made GST-inclusive the same way), never below the selling price.
    /// </summary>
    public static (decimal mrp, decimal selling) Calculate(decimal vendorMrp, decimal vendorPrice, decimal commission, PriceRounding rounding, decimal gstFactor = 1m)
    {
        var cost = (vendorPrice > 0 ? vendorPrice : vendorMrp) * gstFactor;
        var selling = Round(cost * (1 + commission / 100m), rounding);
        var mrp = Math.Max(Math.Round(vendorMrp * gstFactor, 0, MidpointRounding.AwayFromZero), selling);
        return (mrp, selling);
    }

    public static void Apply(Product p, Category? c, BusinessSettings s)
    {
        var commission = EffectiveCommission(p, c, s);
        var factor = GstFactor(p.GstRate, s);
        if (!p.ManualPrice)
            (p.Mrp, p.SellingPrice) = Calculate(p.VendorMrp, p.VendorPrice, commission, s.PriceRounding, factor);

        foreach (var v in p.Variants)
        {
            if (p.ManualPrice && v.SellingPrice > 0) continue;
            (v.Mrp, v.SellingPrice) = Calculate(v.VendorMrp, v.VendorPrice, commission, s.PriceRounding, factor);
        }

        // Keep the product's headline price aligned with its cheapest active variant.
        var activeVariants = p.Variants.Where(v => v.IsActive).ToList();
        if (activeVariants.Count > 0 && !p.ManualPrice)
        {
            var cheapest = activeVariants.MinBy(v => v.SellingPrice)!;
            p.SellingPrice = cheapest.SellingPrice;
            p.Mrp = cheapest.Mrp;
        }
    }

    public async Task<BusinessSettings> GetSettingsAsync() =>
        await db.BusinessSettings.FirstAsync();

    /// <summary>Recalculates prices for products matching the filter (all when null).</summary>
    public async Task<int> RecalculateAsync(int? categoryId = null, IEnumerable<int>? productIds = null)
    {
        var settings = await GetSettingsAsync();
        var q = db.Products.Include(p => p.Variants).Include(p => p.Category).AsQueryable();
        if (categoryId is not null) q = q.Where(p => p.CategoryId == categoryId);
        if (productIds is not null)
        {
            var ids = productIds.ToList();
            q = q.Where(p => ids.Contains(p.Id));
        }
        var products = await q.ToListAsync();
        foreach (var p in products) Apply(p, p.Category, settings);
        await db.SaveChangesAsync();
        return products.Count;
    }
}
