using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

public record KpiDto(decimal Sales, decimal PrevSales, int Orders, int PrevOrders, decimal AvgOrderValue, int NewCustomers, int PrevNewCustomers,
    decimal EstimatedProfit, decimal PrevEstimatedProfit);
public record SeriesPoint(string Label, decimal Value, decimal Value2);
public record NameValue(string Name, decimal Value, int Count);
public record RecentOrderDto(int Id, string OrderNumber, string Customer, DateTime CreatedAt, decimal Total, OrderStatus Status, PaymentStatus PaymentStatus, string City);
public record ActionItemsDto(int OrdersToConfirm, int OrdersToPlaceWithVendor, int OrdersInTransit, int PendingRefunds, int UnpaidVendorBills,
    decimal UnpaidVendorAmount, int PriceChangedProducts, int UnavailableActiveProducts, int NewEnquiries, int PendingReviews, int DeliveredWithoutInvoice, int CodToCollect);
public record DashboardDto(KpiDto Kpis, List<SeriesPoint> SalesSeries, List<NameValue> StatusBreakdown, List<NameValue> TopProducts,
    List<NameValue> SalesByCategory, List<NameValue> TopAreas, List<NameValue> PaymentMethods, List<RecentOrderDto> RecentOrders,
    ActionItemsDto Actions, int ActiveProducts, int TotalCustomers, List<DashboardOfferDto>? Offers = null);

/// <summary>An offer on the dashboard: running, coming up, or ended in the last 30 days.</summary>
public record DashboardOfferDto(int Id, string Name, string Slug, string ThemeColor, string Status, DateTime StartsAt, DateTime EndsAt,
    int ProductsOnOffer, OfferStats Stats);

public record PnlLine(string Label, decimal Amount, string? Note = null);
public record PnlMonth(string Month, decimal Revenue, decimal Cogs, decimal Expenses, decimal NetProfit);
public record OrderProfitDto(int OrderId, string OrderNumber, DateTime Date, string Customer, decimal Revenue, decimal Cost, bool CostIsEstimated, decimal Profit, decimal MarginPercent);
public record ProfitLossDto(
    DateTime From, DateTime To, bool GstRegistered,
    decimal GrossSales, decimal OutputGst, decimal Refunds, decimal NetRevenue,
    decimal CostOfGoods, decimal EstimatedCostPortion, decimal OtherPurchases, decimal GrossProfit, decimal GrossMarginPercent,
    List<PnlLine> Expenses, decimal TotalExpenses, decimal NetProfit, decimal NetMarginPercent,
    decimal InputGst, decimal NetGstPayable,
    decimal CashReceived, decimal CashPaidToVendors, decimal CashExpenses,
    decimal Receivables, decimal Payables,
    int OrdersCount, List<PnlMonth> Monthly, List<OrderProfitDto> Orders,
    decimal OfferDiscounts = 0, decimal CouponDiscounts = 0);

public class ReportService(AppDbContext db)
{
    private static readonly OrderStatus[] Excluded = [OrderStatus.Cancelled];

    public async Task<DashboardDto> GetDashboardAsync(DateTime from, DateTime to)
    {
        var span = to - from;
        var prevFrom = from - span;
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var orders = db.Orders.AsNoTracking().Where(o => !Excluded.Contains(o.Status));

        async Task<(decimal sales, int count, decimal profit)> Totals(DateTime f, DateTime t)
        {
            var r = await orders.Where(o => o.CreatedAt >= f && o.CreatedAt < t)
                .GroupBy(_ => 1).Select(g => new { Sales = g.Sum(o => o.Total), Count = g.Count(), Net = g.Sum(o => o.SubTotal - o.Discount - o.TaxAmount), Cost = g.Sum(o => o.EstimatedCost) })
                .FirstOrDefaultAsync();
            if (r is null) return (0, 0, 0);
            // GST-registered: GST paid to the vendor is reclaimable input credit, so profit uses the cost before GST.
            var cost = r.Cost;
            if (s.GstRegistered)
                cost = await db.OrderItems.Where(i => i.Order!.Status != OrderStatus.Cancelled && i.Order.CreatedAt >= f && i.Order.CreatedAt < t)
                    .SumAsync(i => (decimal?)(i.UnitVendorCost * i.Quantity * 100 / (100 + i.GstRate))) ?? 0;
            return (r.Sales, r.Count, Money.R2(r.Net - cost));
        }

        var cur = await Totals(from, to);
        var prev = await Totals(prevFrom, from);
        var newCustomers = await db.Users.CountAsync(u => u.Role == UserRole.Customer && u.CreatedAt >= from && u.CreatedAt < to);
        var prevNewCustomers = await db.Users.CountAsync(u => u.Role == UserRole.Customer && u.CreatedAt >= prevFrom && u.CreatedAt < from);

        // Daily series (IST dates)
        var raw = await orders.Where(o => o.CreatedAt >= prevFrom && o.CreatedAt < to).Select(o => new { o.CreatedAt, o.Total }).ToListAsync();
        var days = Math.Max(1, (int)Math.Ceiling(span.TotalDays));
        var series = new List<SeriesPoint>();
        for (var i = 0; i < days; i++)
        {
            var d0 = from.AddDays(i); var d1 = d0.AddDays(1);
            var p0 = prevFrom.AddDays(i); var p1 = p0.AddDays(1);
            series.Add(new SeriesPoint(FinancialYear.ToIst(d0).ToString("dd MMM"),
                raw.Where(o => o.CreatedAt >= d0 && o.CreatedAt < d1).Sum(o => o.Total),
                raw.Where(o => o.CreatedAt >= p0 && o.CreatedAt < p1).Sum(o => o.Total)));
        }

        var inRange = db.Orders.AsNoTracking().Where(o => o.CreatedAt >= from && o.CreatedAt < to);
        var status = (await inRange.GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count(), Sum = g.Sum(o => o.Total) }).ToListAsync())
            .Select(x => new NameValue(x.Key.ToString(), x.Sum, x.Count)).ToList();

        var items = db.OrderItems.AsNoTracking().Where(i => i.Order!.CreatedAt >= from && i.Order.CreatedAt < to && i.Order.Status != OrderStatus.Cancelled);
        var topProducts = (await items.GroupBy(i => i.ProductName).Select(g => new { g.Key, Sum = g.Sum(i => i.LineTotal), Qty = g.Sum(i => i.Quantity) })
            .OrderByDescending(x => x.Sum).Take(8).ToListAsync()).Select(x => new NameValue(x.Key, x.Sum, x.Qty)).ToList();
        var byCategory = (await items.GroupBy(i => i.Product!.Category!.Name).Select(g => new { g.Key, Sum = g.Sum(i => i.LineTotal), Qty = g.Sum(i => i.Quantity) })
            .OrderByDescending(x => x.Sum).Take(10).ToListAsync()).Select(x => new NameValue(x.Key, x.Sum, x.Qty)).ToList();

        var ok = inRange.Where(o => o.Status != OrderStatus.Cancelled);
        var areas = (await ok.GroupBy(o => o.ShipPincode).Select(g => new { g.Key, Sum = g.Sum(o => o.Total), Count = g.Count() })
            .OrderByDescending(x => x.Sum).Take(8).ToListAsync()).ToList();
        var pinNames = await db.ServiceablePincodes.AsNoTracking().Where(p => areas.Select(a => a.Key).Contains(p.Pincode)).ToDictionaryAsync(p => p.Pincode, p => p.Area);
        var topAreas = areas.Select(a => new NameValue(pinNames.GetValueOrDefault(a.Key) is { } n ? $"{n} ({a.Key})" : a.Key, a.Sum, a.Count)).ToList();
        var methods = (await ok.GroupBy(o => o.PaymentMethod).Select(g => new { g.Key, Sum = g.Sum(o => o.Total), Count = g.Count() }).ToListAsync())
            .Select(x => new NameValue(x.Key.ToString(), x.Sum, x.Count)).ToList();

        var recent = await db.Orders.AsNoTracking().OrderByDescending(o => o.CreatedAt).Take(8)
            .Select(o => new RecentOrderDto(o.Id, o.OrderNumber, o.ShipName, o.CreatedAt, o.Total, o.Status, o.PaymentStatus, o.ShipCity)).ToListAsync();

        var actions = new ActionItemsDto(
            await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending),
            await db.Orders.CountAsync(o => o.Status == OrderStatus.Confirmed),
            await db.Orders.CountAsync(o => o.Status == OrderStatus.PlacedWithVendor || o.Status == OrderStatus.Dispatched || o.Status == OrderStatus.OutForDelivery),
            await db.Refunds.CountAsync(r => r.Status == RefundStatus.Requested || r.Status == RefundStatus.Approved),
            await db.VendorBills.CountAsync(b => b.PaymentStatus != BillPaymentStatus.Paid),
            await db.VendorBills.Where(b => b.PaymentStatus != BillPaymentStatus.Paid).SumAsync(b => (decimal?)(b.TotalAmount - b.AmountPaid)) ?? 0,
            await db.Products.CountAsync(p => p.VendorPriceChanged),
            await db.Products.CountAsync(p => p.IsActive && !p.IsAvailable),
            await db.Enquiries.CountAsync(e => e.Status == EnquiryStatus.New),
            await db.Reviews.CountAsync(r => !r.IsApproved),
            await db.Orders.CountAsync(o => o.Status == OrderStatus.Delivered && !o.Invoices.Any(i => !i.IsCancelled)),
            await db.Orders.CountAsync(o => o.Status == OrderStatus.Delivered && o.PaymentStatus != PaymentStatus.Paid && o.PaymentMethod == PaymentMethod.CashOnDelivery));

        // Festival offers
        var now = DateTime.UtcNow;
        var recentOffers = await db.Offers.AsNoTracking().Where(o => o.IsActive && o.EndsAt > now.AddDays(-30))
            .OrderBy(o => o.EndsAt <= now).ThenBy(o => o.StartsAt).Take(6).ToListAsync();
        var offerIds = recentOffers.Select(o => o.Id).ToList();
        var offerStats = await new OfferService(db).StatsAsync(offerIds);
        var onOffer = await db.Products.AsNoTracking().Where(p => p.IsActive && p.OfferId != null && p.OfferPrice != null && offerIds.Contains(p.OfferId.Value))
            .GroupBy(p => p.OfferId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var offers = recentOffers.Select(o => new DashboardOfferDto(o.Id, o.Name, o.Slug, o.ThemeColor, CatalogService.OfferStatus(o, now),
            o.StartsAt, o.EndsAt, onOffer.GetValueOrDefault(o.Id), offerStats.GetValueOrDefault(o.Id) ?? OfferStats.Empty)).ToList();

        return new DashboardDto(
            new KpiDto(cur.sales, prev.sales, cur.count, prev.count, cur.count == 0 ? 0 : Money.R2(cur.sales / cur.count),
                newCustomers, prevNewCustomers, cur.profit, prev.profit),
            series, status, topProducts, byCategory, topAreas, methods, recent, actions,
            await db.Products.CountAsync(p => p.IsActive && p.IsAvailable),
            await db.Users.CountAsync(u => u.Role == UserRole.Customer),
            offers);
    }

    /// <summary>
    /// Profit &amp; loss on an accrual basis: revenue is recognised on delivery.
    /// Cost of goods uses the vendor bill linked to each order; when no bill has been entered yet the vendor
    /// price captured at order time is used and flagged as estimated.
    /// </summary>
    public async Task<ProfitLossDto> GetProfitLossAsync(DateTime from, DateTime to)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var gst = s.GstRegistered;

        var delivered = await db.Orders.AsNoTracking()
            .Where(o => (o.Status == OrderStatus.Delivered || o.Status == OrderStatus.Returned) && o.DeliveredAt >= from && o.DeliveredAt < to)
            .Select(o => new
            {
                o.Id, o.OrderNumber, o.ShipName, o.DeliveredAt, o.Total, o.TaxAmount, o.EstimatedCost, o.Status, o.OfferDiscount, o.Discount,
                CostExGst = o.Items.Sum(i => i.UnitVendorCost * i.Quantity * 100 / (100 + i.GstRate))
            })
            .ToListAsync();
        var orderIds = delivered.Select(o => o.Id).ToList();

        var bills = await db.VendorBills.AsNoTracking().Where(b => b.OrderId != null && orderIds.Contains(b.OrderId.Value)).ToListAsync();
        var billByOrder = bills.GroupBy(b => b.OrderId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(b => gst ? b.TaxableAmount + b.ShippingCharges : b.TotalAmount));
        var otherBills = await db.VendorBills.AsNoTracking().Where(b => b.OrderId == null && b.BillDate >= from && b.BillDate < to).ToListAsync();

        var refunds = await db.Refunds.AsNoTracking().Where(r => r.Status == RefundStatus.Processed && r.ProcessedAt >= from && r.ProcessedAt < to).SumAsync(r => (decimal?)r.Amount) ?? 0;

        var orderProfits = delivered.Select(o =>
        {
            var revenue = o.Total - o.TaxAmount;
            var hasBill = billByOrder.TryGetValue(o.Id, out var billed);
            var cost = hasBill ? billed : Money.R2(gst ? o.CostExGst : o.EstimatedCost);
            var profit = revenue - cost;
            return new OrderProfitDto(o.Id, o.OrderNumber, o.DeliveredAt!.Value, o.ShipName, revenue, cost, !hasBill, profit,
                revenue == 0 ? 0 : Money.R2(profit / revenue * 100));
        }).OrderByDescending(o => o.Date).ToList();

        var grossSales = delivered.Sum(o => o.Total);
        var outputGst = delivered.Sum(o => o.TaxAmount);
        var netRevenue = grossSales - outputGst - refunds;
        var otherPurchases = otherBills.Sum(b => gst ? b.TaxableAmount + b.ShippingCharges : b.TotalAmount);
        var cogs = orderProfits.Sum(o => o.Cost) + otherPurchases;
        var estimatedPortion = orderProfits.Where(o => o.CostIsEstimated).Sum(o => o.Cost);
        var grossProfit = netRevenue - cogs;

        var expenses = await db.Expenses.AsNoTracking().Where(e => e.Date >= from && e.Date < to).ToListAsync();
        var expenseLines = expenses.GroupBy(e => e.Category)
            .Select(g => new PnlLine(Humanize(g.Key.ToString()), g.Sum(e => gst ? e.Amount : e.Amount + e.GstAmount)))
            .OrderByDescending(l => l.Amount).ToList();
        var totalExpenses = expenseLines.Sum(l => l.Amount);
        var netProfit = grossProfit - totalExpenses;

        var inputGst = gst ? bills.Concat(otherBills).Sum(b => b.GstAmount) + expenses.Sum(e => e.GstAmount) : 0;

        var cashReceived = await db.Payments.AsNoTracking().Where(p => p.IsSuccessful && p.PaidAt >= from && p.PaidAt < to).SumAsync(p => (decimal?)p.Amount) ?? 0;
        var cashToVendors = await db.VendorBills.AsNoTracking().Where(b => b.LastPaidOn >= from && b.LastPaidOn < to).SumAsync(b => (decimal?)b.AmountPaid) ?? 0;
        var cashExpenses = expenses.Sum(e => e.Amount + e.GstAmount);
        var receivables = await db.Orders.AsNoTracking().Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Pending && o.AmountPaid < o.Total)
            .SumAsync(o => (decimal?)(o.Total - o.AmountPaid)) ?? 0;
        var payables = await db.VendorBills.AsNoTracking().Where(b => b.PaymentStatus != BillPaymentStatus.Paid).SumAsync(b => (decimal?)(b.TotalAmount - b.AmountPaid)) ?? 0;

        // Monthly breakdown
        var monthly = new List<PnlMonth>();
        for (var m = new DateTime(from.Year, from.Month, 1, 0, 0, 0, DateTimeKind.Utc); m < to; m = m.AddMonths(1))
        {
            var mEnd = m.AddMonths(1);
            var mOrders = orderProfits.Where(o => o.Date >= m && o.Date < mEnd).ToList();
            var mOther = otherBills.Where(b => b.BillDate >= m && b.BillDate < mEnd).Sum(b => gst ? b.TaxableAmount + b.ShippingCharges : b.TotalAmount);
            var mExp = expenses.Where(e => e.Date >= m && e.Date < mEnd).Sum(e => gst ? e.Amount : e.Amount + e.GstAmount);
            var rev = mOrders.Sum(o => o.Revenue);
            var cost = mOrders.Sum(o => o.Cost) + mOther;
            monthly.Add(new PnlMonth(m.ToString("MMM yyyy"), rev, cost, mExp, rev - cost - mExp));
        }

        return new ProfitLossDto(from, to, gst, grossSales, outputGst, refunds, netRevenue,
            cogs, estimatedPortion, otherPurchases, grossProfit, netRevenue == 0 ? 0 : Money.R2(grossProfit / netRevenue * 100),
            expenseLines, totalExpenses, netProfit, netRevenue == 0 ? 0 : Money.R2(netProfit / netRevenue * 100),
            inputGst, outputGst - inputGst, cashReceived, cashToVendors, cashExpenses, receivables, payables,
            delivered.Count, monthly, orderProfits,
            delivered.Sum(o => o.OfferDiscount), delivered.Sum(o => o.Discount));
    }

    private static string Humanize(string s) => string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
}
