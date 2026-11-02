using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

public record OfferApplyResult(int Products, int LimitedByMargin, List<OfferWindow>? NextWindows = null);
public record OfferWindow(DateTime StartsAt, DateTime EndsAt);
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
    public static bool IsLive(Offer o, DateTime now) => OfferSchedule.Current(o, now) is not null;

    /// <summary>
    /// Offer price for one item, or null when the offer can't reduce it. Never below our GST-inclusive cost plus the
    /// offer's minimum margin (items without a vendor cost, e.g. own products, are not limited).
    /// </summary>
    public static (decimal? price, bool limited) PriceFor(decimal regular, decimal vendorPrice, decimal gstRate, Offer o, BusinessSettings s)
    {
        if (regular <= 0 || o.Value <= 0) return (null, false);
        var off = o.DiscountType == DiscountType.Percent ? regular * o.Value / 100m : o.Value;
        if (o.DiscountType == DiscountType.Percent && o.MaxDiscount is > 0) off = Math.Min(off, o.MaxDiscount.Value);
        var price = RoundDown(Math.Floor(regular - off), o.Rounding);
        var limited = false;
        if (vendorPrice > 0)
        {
            var floor = Math.Ceiling(PricingService.CostOf(vendorPrice, gstRate, s) * (1 + o.MinMarginPercent / 100m));
            if (price < floor) { price = floor; limited = true; }
        }
        return price > 0 && price < regular ? (price, limited) : (null, limited);
    }

    /// <summary>Offer rounding goes down (12,345 → 12,299 / 12,340 / 12,300), so the discount is never smaller than advertised.</summary>
    public static decimal RoundDown(decimal price, PriceRounding rounding) => rounding switch
    {
        PriceRounding.Nearest10 => Math.Floor(price / 10m) * 10m,
        PriceRounding.Nearest100 => Math.Floor(price / 100m) * 100m,
        PriceRounding.EndWith99 => Math.Floor((price + 1) / 100m) * 100m - 1,
        _ => price
    };

    public record LiveOffer(Offer Offer, HashSet<int> CategoryIds, HashSet<int> ProductIds, HashSet<int> ExcludedIds)
    {
        public bool Covers(Product p) => !ExcludedIds.Contains(p.Id) && Offer.Scope switch
        {
            OfferScope.Categories => CategoryIds.Contains(p.CategoryId),
            OfferScope.Products => ProductIds.Contains(p.Id),
            _ => true
        };
    }

    /// <summary>Enabled offers whose dates haven't passed, with what they cover (the schedule decides whether they are running).</summary>
    public async Task<List<LiveOffer>> CandidatesAsync(DateTime now, CancellationToken ct = default) =>
        (await db.Offers.AsNoTracking().Where(o => o.IsActive && o.EndsAt > now.AddDays(-1))
                .OrderBy(o => o.SortOrder).ThenBy(o => o.Id)
                .Select(o => new
                {
                    Offer = o, Cats = o.Categories.Select(c => c.Id).ToList(), Prods = o.Products.Select(p => p.Id).ToList(),
                    Excl = o.ExcludedProducts.Select(p => p.Id).ToList()
                })
                .ToListAsync(ct))
            .Select(x => new LiveOffer(x.Offer, x.Cats.ToHashSet(), x.Prods.ToHashSet(), x.Excl.ToHashSet())).ToList();

    /// <summary>Recalculates the offer price of every product (the best live offer wins when several overlap).</summary>
    public async Task<OfferApplyResult> ApplyAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync(ct);
        var live = (await CandidatesAsync(now, ct)).Where(lo => IsLive(lo.Offer, now)).ToList();

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
    public async Task<OfferApplyResult> PreviewAsync(Offer o, List<int> categoryIds, List<int> productIds, List<int> excludedIds)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var q = db.Products.AsNoTracking().Where(p => p.IsActive && p.SellingPrice > 0);
        if (o.Scope == OfferScope.Categories) q = q.Where(p => categoryIds.Contains(p.CategoryId));
        if (o.Scope == OfferScope.Products) q = q.Where(p => productIds.Contains(p.Id));
        else if (excludedIds.Count > 0) q = q.Where(p => !excludedIds.Contains(p.Id));
        var rows = await q.Select(p => new { p.SellingPrice, p.VendorPrice, p.GstRate }).ToListAsync();
        var results = rows.Select(r => PriceFor(r.SellingPrice, r.VendorPrice, r.GstRate, o, s)).ToList();
        return new OfferApplyResult(results.Count(r => r.price is not null), results.Count(r => r.limited), OfferSchedule.Upcoming(o, DateTime.UtcNow, 5));
    }
}

/// <summary>
/// Keeps offer prices current: re-applies when an offer (or a flash-deal window) starts or ends, or when product prices change.
/// It wakes exactly when the next window opens or closes, so a 5-minute deal starts and ends on time, and clears the catalog cache then.
/// </summary>
public class OfferPriceWorker(IServiceScopeFactory scopes, ILogger<OfferPriceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? last = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = TimeSpan.FromSeconds(30);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = DateTime.UtcNow;
                var offers = await db.Offers.AsNoTracking().Where(o => o.IsActive && o.EndsAt > now.AddDays(-1)).ToListAsync(stoppingToken);
                var windows = string.Join(",", offers.Select(o => (o, w: OfferSchedule.Current(o, now))).Where(x => x.w is not null)
                    .OrderBy(x => x.o.Id).Select(x => $"{x.o.Id}:{x.w!.StartsAt.Ticks}:{(x.o.UpdatedAt ?? x.o.CreatedAt).Ticks}"));
                // Products changed by vendor sync / price edits since the last run also need new offer prices.
                var productsChanged = await db.Products.MaxAsync(p => (DateTime?)(p.UpdatedAt ?? p.CreatedAt), stoppingToken);
                if (windows + "|" + productsChanged?.Ticks != last)
                {
                    var r = await scope.ServiceProvider.GetRequiredService<OfferService>().ApplyAsync(stoppingToken);
                    if (windows.Length > 0 || last is not null) logger.LogInformation("Offer prices updated: {Count} products on offer", r.Products);
                    // A window opened or closed: drop cached catalog pages so customers see the new prices straight away.
                    if (last is not null && windows != last.Split('|')[0])
                    {
                        if (scope.ServiceProvider.GetService<ICatalogCache>() is { } cache) await cache.ClearAsync();
                        // A flash deal that just opened sends its app notification now (the 10-minute round would be too late).
                        var hour = TimeZoneInfo.ConvertTimeFromUtc(now, Push.PushAutomation.India).Hour;
                        if (windows.Length > 0 && hour is >= 9 and < 21)
                        {
                            var run = new Push.PushAutomation(db, scope.ServiceProvider.GetRequiredService<Push.PushService>());
                            await run.FlashCartsAsync(); // people with the product in their cart first, with the deadline
                            await run.OffersAsync();
                        }
                    }
                    // Applying can itself update products; take the signature after applying so it doesn't loop.
                    var after = await db.Products.MaxAsync(p => (DateTime?)(p.UpdatedAt ?? p.CreatedAt), stoppingToken);
                    last = windows + "|" + after?.Ticks;
                }
                var change = offers.Select(o => OfferSchedule.NextChange(o, now)).Where(t => t is not null).Min();
                if (change is not null && change.Value - now < wait) wait = change.Value - now + TimeSpan.FromMilliseconds(200);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Updating offer prices failed"); }
            try { await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}

/// <summary>Clears the storefront's cached catalog responses (the API's output cache implements it).</summary>
public interface ICatalogCache
{
    Task ClearAsync();
}

/// <summary>
/// When an offer runs. One-time offers run from StartsAt to EndsAt. Repeating offers (flash deals) run for WindowMinutes from
/// WindowStart (India time) on every matching day whose window starts between StartsAt and EndsAt.
/// </summary>
public static class OfferSchedule
{
    private static readonly TimeZoneInfo India = Push.PushAutomation.India;
    private const int SearchDays = 400;

    public static List<int> Numbers(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n >= 0).Distinct().Order().ToList();

    public static bool Repeats(Offer o) => o.Repeat != OfferRepeat.Once && o.WindowStart is not null && o.WindowMinutes > 0;

    /// <summary>Does a repeating offer have a window on this India date?</summary>
    public static bool RunsOn(Offer o, DateOnly day)
    {
        var days = Numbers(o.RepeatDays);
        switch (o.Repeat)
        {
            case OfferRepeat.Daily: return true;
            case OfferRepeat.Weekly: return days.Contains((int)day.DayOfWeek);
            case OfferRepeat.MonthlyDates: return days.Contains(day.Day);
            case OfferRepeat.MonthlyWeekday:
                if (!days.Contains((int)day.DayOfWeek)) return false;
                var weeks = Numbers(o.RepeatWeeks);
                var last = day.Day + 7 > DateTime.DaysInMonth(day.Year, day.Month);
                return weeks.Contains((day.Day - 1) / 7 + 1) || (last && weeks.Contains(5));
            default: return false;
        }
    }

    private static OfferWindow? WindowOn(Offer o, DateOnly day)
    {
        if (!RunsOn(o, day)) return null;
        var local = day.ToDateTime(TimeOnly.FromTimeSpan(o.WindowStart!.Value), DateTimeKind.Unspecified);
        var start = TimeZoneInfo.ConvertTimeToUtc(local, India);
        return start >= o.StartsAt && start < o.EndsAt ? new OfferWindow(start, start.AddMinutes(o.WindowMinutes)) : null;
    }

    private static DateOnly IndiaDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), India));

    /// <summary>The window running now, or null.</summary>
    public static OfferWindow? Current(Offer o, DateTime now)
    {
        if (!o.IsActive) return null;
        if (!Repeats(o)) return o.StartsAt <= now && o.EndsAt > now ? new OfferWindow(o.StartsAt, o.EndsAt) : null;
        var today = IndiaDate(now);
        // A window can run past midnight, so yesterday's may still be open.
        foreach (var day in new[] { today.AddDays(-1), today })
            if (WindowOn(o, day) is { } w && w.StartsAt <= now && w.EndsAt > now) return w;
        return null;
    }

    /// <summary>The next windows that haven't started yet.</summary>
    public static List<OfferWindow> Upcoming(Offer o, DateTime now, int count)
    {
        var list = new List<OfferWindow>();
        if (!Repeats(o))
        {
            if (o.StartsAt > now && o.EndsAt > o.StartsAt) list.Add(new OfferWindow(o.StartsAt, o.EndsAt));
            return list;
        }
        var first = IndiaDate(o.StartsAt > now ? o.StartsAt : now);
        var lastDay = IndiaDate(o.EndsAt);
        for (var day = first; list.Count < count && day <= lastDay && day <= first.AddDays(SearchDays); day = day.AddDays(1))
            if (WindowOn(o, day) is { } w && w.StartsAt > now) list.Add(w);
        return list;
    }

    public static OfferWindow? Next(Offer o, DateTime now) => o.IsActive ? Upcoming(o, now, 1).FirstOrDefault() : null;

    /// <summary>When this offer next starts or stops (for waking the price worker on time).</summary>
    public static DateTime? NextChange(Offer o, DateTime now)
    {
        var end = Current(o, now)?.EndsAt;
        var start = Next(o, now)?.StartsAt;
        return end is null ? start : start is null ? end : (end < start ? end : start);
    }

    public static string Status(Offer o, DateTime now) =>
        !o.IsActive ? "off" : Current(o, now) is not null ? "live" : Next(o, now) is not null ? "upcoming" : "ended";

    /// <summary>A flash deal: a repeating offer, or a one-time offer of a day or less (the storefront shows a ticking clock).</summary>
    public static bool IsFlash(Offer o) => Repeats(o) || o.EndsAt - o.StartsAt <= TimeSpan.FromDays(1);

    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] WeekNames = ["", "1st", "2nd", "3rd", "4th", "last"];

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static string Join(IEnumerable<string> parts)
    {
        var l = parts.ToList();
        return l.Count <= 1 ? string.Concat(l) : string.Join(", ", l[..^1]) + " and " + l[^1];
    }

    public static string Duration(int minutes) => minutes % 60 == 0
        ? (minutes == 60 ? "1 hour" : $"{minutes / 60} hours")
        : minutes > 60 ? $"{minutes / 60} hr {minutes % 60} min" : $"{minutes} minutes";

    /// <summary>"Every Monday, 8:00 pm for 15 minutes"; null for one-time offers.</summary>
    public static string? Describe(Offer o)
    {
        if (!Repeats(o)) return null;
        var days = Numbers(o.RepeatDays);
        var when = o.Repeat switch
        {
            OfferRepeat.Daily => "Every day",
            OfferRepeat.Weekly => days.Count == 7 ? "Every day" : "Every " + Join(days.Where(d => d < 7).Select(d => DayNames[d])),
            OfferRepeat.MonthlyWeekday => "Every " + Join(Numbers(o.RepeatWeeks).Where(w => w is >= 1 and <= 5).Select(w => WeekNames[w]))
                                          + " " + Join(days.Where(d => d < 7).Select(d => DayNames[d])) + " of the month",
            OfferRepeat.MonthlyDates => "Every month on the " + Join(days.Where(d => d is >= 1 and <= 31).Select(Ordinal)),
            _ => ""
        };
        var t = DateTime.Today.Add(o.WindowStart!.Value).ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
        return $"{when}, {t} for {Duration(o.WindowMinutes)}";
    }
}
