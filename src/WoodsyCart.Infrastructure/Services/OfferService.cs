using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

public record OfferApplyResult(int Products, int LimitedByMargin);
public record OfferStats(int Orders, int Units, decimal Revenue, decimal DiscountGiven, decimal Margin)
{
    public static readonly OfferStats Empty = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Festival offers. The offer price of every product is stored on the product (Product.OfferPrice / Variant.OfferPrice)
/// so the storefront can filter, sort and show it like any other price. <see cref="OfferPriceWorker"/> keeps it current:
/// offers switch on and off by their dates, and prices follow vendor-sync and commission changes.
/// </summary>
public class OfferService(AppDbContext db)
{
    public static bool IsLive(Offer o, DateTime now) => o.IsActive && o.StartsAt <= now && o.EndsAt > now;

    /// <summary>
    /// Offer price for one item, or null when the offer can't reduce it. Never below our GST-inclusive cost plus the
    /// offer's minimum margin (items without a vendor cost, e.g. own products, are not limited).
    /// </summary>
    public static (decimal? price, bool limited) PriceFor(decimal regular, decimal vendorPrice, decimal gstRate, Offer o, BusinessSettings s)
    {
        if (regular <= 0 || o.Value <= 0) return (null, false);
        var off = o.DiscountType == DiscountType.Percent ? regular * o.Value / 100m : o.Value;
        if (o.DiscountType == DiscountType.Percent && o.MaxDiscount is > 0) off = Math.Min(off, o.MaxDiscount.Value);
        var price = Math.Floor(regular - off);
        var limited = false;
        if (vendorPrice > 0)
        {
            var floor = Math.Ceiling(PricingService.CostOf(vendorPrice, gstRate, s) * (1 + o.MinMarginPercent / 100m));
            if (price < floor) { price = floor; limited = true; }
        }
        return price > 0 && price < regular ? (price, limited) : (null, limited);
    }

    private record LiveOffer(Offer Offer, HashSet<int> CategoryIds, HashSet<int> ProductIds)
    {
        public bool Covers(Product p) => Offer.Scope switch
        {
            OfferScope.Categories => CategoryIds.Contains(p.CategoryId),
            OfferScope.Products => ProductIds.Contains(p.Id),
            _ => true
        };
    }

    /// <summary>Recalculates the offer price of every product (the best live offer wins when several overlap).</summary>
    public async Task<OfferApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync(ct);
        var live = (await db.Offers.AsNoTracking().Where(o => o.IsActive && o.StartsAt <= now && o.EndsAt > now)
                .OrderBy(o => o.SortOrder).ThenBy(o => o.Id)
                .Select(o => new { Offer = o, Cats = o.Categories.Select(c => c.Id).ToList(), Prods = o.Products.Select(p => p.Id).ToList() })
                .ToListAsync(ct))
            .Select(x => new LiveOffer(x.Offer, x.Cats.ToHashSet(), x.Prods.ToHashSet())).ToList();

        // Only products that have, or may get, an offer price need loading.
        var q = db.Products.Include(p => p.Variants).AsSplitQuery();
        if (live.Count == 0) q = q.Where(p => p.OfferId != null || p.OfferPrice != null || p.Variants.Any(v => v.OfferPrice != null));
        var products = await q.ToListAsync(ct);

        int applied = 0, limitedCount = 0;
        foreach (var p in products)
        {
            (LiveOffer offer, decimal price)? best = null;
            var limitedHere = false;
            foreach (var lo in live.Where(lo => lo.Covers(p)))
            {
                var (price, limited) = PriceFor(p.SellingPrice, p.VendorPrice, p.GstRate, lo.Offer, s);
                limitedHere |= limited;
                if (price is not null && (best is null || price < best.Value.price)) best = (lo, price.Value);
            }
            if (limitedHere) limitedCount++;

            var offerId = best?.offer.Offer.Id;
            if (p.OfferId != offerId) p.OfferId = offerId;
            if (p.OfferPrice != best?.price) p.OfferPrice = best?.price;
            foreach (var v in p.Variants)
            {
                var vp = best is null ? null : PriceFor(v.SellingPrice, v.VendorPrice, p.GstRate, best.Value.offer.Offer, s).price;
                if (v.OfferPrice != vp) v.OfferPrice = vp;
            }
            if (best is not null && p.IsActive) applied++; // count what customers can see
        }
        await db.SaveChangesAsync(ct);
        return new OfferApplyResult(applied, limitedCount);
    }

    /// <summary>Sales made under each offer (cancelled orders excluded). Margin is before shipping and expenses.</summary>
    public async Task<Dictionary<int, OfferStats>> StatsAsync(IEnumerable<int>? offerIds = null)
    {
        var gst = (await db.BusinessSettings.AsNoTracking().FirstAsync()).GstRegistered;
        var items = db.OrderItems.AsNoTracking().Where(i => i.OfferId != null && i.Order!.Status != OrderStatus.Cancelled);
        if (offerIds is not null)
        {
            var ids = offerIds.ToList();
            items = items.Where(i => ids.Contains(i.OfferId!.Value));
        }
        var rows = await items.GroupBy(i => i.OfferId!.Value).Select(g => new
        {
            Id = g.Key,
            Orders = g.Select(i => i.OrderId).Distinct().Count(),
            Units = g.Sum(i => i.Quantity),
            Revenue = g.Sum(i => i.LineTotal),
            Discount = g.Sum(i => (i.UnitRegularPrice - i.UnitPrice) * i.Quantity),
            // GST-registered: GST on both sides is passed through, so margin is compared before tax.
            Margin = gst
                ? g.Sum(i => (i.LineTotal - i.UnitVendorCost * i.Quantity) * 100 / (100 + i.GstRate))
                : g.Sum(i => i.LineTotal - i.UnitVendorCost * i.Quantity)
        }).ToListAsync();
        return rows.ToDictionary(r => r.Id, r => new OfferStats(r.Orders, r.Units, r.Revenue, Math.Round(r.Discount, 2), Math.Round(r.Margin, 2)));
    }

    /// <summary>How many products an offer would cover and reduce, without saving anything (for the admin form).</summary>
    public async Task<OfferApplyResult> PreviewAsync(Offer o, List<int> categoryIds, List<int> productIds)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var q = db.Products.AsNoTracking().Where(p => p.IsActive && p.SellingPrice > 0);
        if (o.Scope == OfferScope.Categories) q = q.Where(p => categoryIds.Contains(p.CategoryId));
        if (o.Scope == OfferScope.Products) q = q.Where(p => productIds.Contains(p.Id));
        var rows = await q.Select(p => new { p.SellingPrice, p.VendorPrice, p.GstRate }).ToListAsync();
        var results = rows.Select(r => PriceFor(r.SellingPrice, r.VendorPrice, r.GstRate, o, s)).ToList();
        return new OfferApplyResult(results.Count(r => r.price is not null), results.Count(r => r.limited));
    }
}

/// <summary>Keeps offer prices current: re-applies when an offer starts or ends, or when product prices change.</summary>
public class OfferPriceWorker(IServiceScopeFactory scopes, ILogger<OfferPriceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? last = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var live = await db.Offers.AsNoTracking().Where(o => o.IsActive && o.StartsAt <= now && o.EndsAt > now)
                    .OrderBy(o => o.Id).Select(o => o.Id + ":" + (o.UpdatedAt ?? o.CreatedAt).Ticks).ToListAsync(stoppingToken);
                // Products changed by vendor sync / price edits since the last run also need new offer prices.
                var productsChanged = await db.Products.MaxAsync(p => (DateTime?)(p.UpdatedAt ?? p.CreatedAt), stoppingToken);
                var signature = string.Join(",", live) + "|" + productsChanged?.Ticks;
                if (signature != last)
                {
                    var r = await scope.ServiceProvider.GetRequiredService<OfferService>().ApplyAsync(stoppingToken);
                    if (live.Count > 0 || last is not null) logger.LogInformation("Offer prices updated: {Count} products on offer", r.Products);
                    // Applying can itself update products; take the signature after applying so it doesn't loop.
                    var after = await db.Products.MaxAsync(p => (DateTime?)(p.UpdatedAt ?? p.CreatedAt), stoppingToken);
                    last = string.Join(",", live) + "|" + after?.Ticks;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Updating offer prices failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
