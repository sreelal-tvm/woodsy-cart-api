using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>
/// "Selling fast" comes only from real sales: products ordered in at least SellingFastOrders separate orders in the last
/// SellingFastDays days (cancelled/returned orders and unpaid ones don't count). Admin's own label is Product.IsPick, which says
/// "Woodsy pick" (BusinessSettings.PickLabel) — never "selling fast", so the label stays true.
/// </summary>
public class SellingFastService(AppDbContext db)
{
    public const int MaxDays = 90;

    /// <summary>Words that would claim popularity or sales; the Admin's own label can't use them (dark-pattern rules on false popularity).</summary>
    private static readonly string[] SalesWords = ["sell", "sold", "fast", "best", "popular", "trend", "demand", "hot", "hurry"];

    public static string? PickLabelProblem(string? label)
    {
        var l = label?.Trim() ?? "";
        if (l.Length is < 2 or > 24) return "The label for your own picks must be 2 to 24 characters.";
        return SalesWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))
            ? "Your own label can't claim sales or popularity (\"selling fast\", \"bestseller\", \"trending\"…) — those come only from real orders. Try \"Woodsy pick\" or \"Staff favourite\"."
            : null;
    }

    /// <summary>Recounts recent orders for every product; returns how many products are selling fast.</summary>
    public async Task<int> RefreshAsync()
    {
        var s = await db.BusinessSettings.AsNoTracking().Select(x => new { x.SellingFastOrders, x.SellingFastDays }).FirstAsync();
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(s.SellingFastDays, 1, MaxDays));
        var counts = await db.OrderItems.AsNoTracking()
            .Where(i => i.Order!.CreatedAt >= since && i.Order.Status != OrderStatus.Cancelled && i.Order.Status != OrderStatus.Returned
                        && (i.Order.Status != OrderStatus.Pending || i.Order.PaymentStatus == PaymentStatus.Paid || i.Order.PaymentStatus == PaymentStatus.PartiallyPaid))
            .GroupBy(i => i.ProductId)
            .Select(g => new { ProductId = g.Key, Orders = g.Select(i => i.OrderId).Distinct().Count() })
            .ToDictionaryAsync(x => x.ProductId, x => x.Orders);

        var ids = counts.Keys.ToList();
        var rows = await db.Products.IgnoreQueryFilters()
            .Where(p => p.RecentOrders != 0 || p.IsSellingFast || ids.Contains(p.Id))
            .Select(p => new { p.Id, p.RecentOrders, p.IsSellingFast }).ToListAsync();
        foreach (var r in rows)
        {
            var n = counts.GetValueOrDefault(r.Id);
            var fast = s.SellingFastOrders > 0 && n >= s.SellingFastOrders;
            if (n == r.RecentOrders && fast == r.IsSellingFast) continue;
            await db.Products.IgnoreQueryFilters().Where(p => p.Id == r.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.RecentOrders, n).SetProperty(p => p.IsSellingFast, fast));
        }
        return counts.Count(c => s.SellingFastOrders > 0 && c.Value >= s.SellingFastOrders);
    }
}

/// <summary>Keeps "Selling fast" up to date: every hour (and straight after Admin changes the setting).</summary>
public class SellingFastWorker(IServiceScopeFactory scopes, ILogger<SellingFastWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SellingFastService>().RefreshAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Selling fast refresh failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
