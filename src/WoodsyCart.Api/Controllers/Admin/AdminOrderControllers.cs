using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

public record AdminOrderRow(int Id, string OrderNumber, DateTime CreatedAt, int UserId, string Customer, string Phone, string City, string Pincode,
    OrderStatus Status, PaymentStatus PaymentStatus, PaymentMethod PaymentMethod, decimal Total, decimal AmountPaid, decimal EstimatedCost,
    int ItemCount, bool HasPurchaseOrder, bool HasVendorBill, bool HasInvoice, SalesChannel Channel = SalesChannel.Website);

public record AdminOrderItem(int Id, int ProductId, int? VariantId, string ProductName, string? VariantName, string? ImageUrl, int Quantity,
    decimal UnitMrp, decimal UnitPrice, decimal LineTotal, decimal UnitVendorPrice, decimal UnitVendorCost, decimal GstRate, string? HsnCode, long? VendorProductId, string? VendorUrl);
public record AdminHistory(int Id, OrderStatus Status, string? Note, bool VisibleToCustomer, string? CreatedBy, DateTime CreatedAt);
public record AdminPayment(int Id, PaymentMethod Method, decimal Amount, bool IsSuccessful, string? GatewayPaymentId, string? Reference, DateTime PaidAt, string? Notes);
public record AdminRefund(int Id, RefundType Type, RefundStatus Status, decimal Amount, string Reason, string? CustomerComment, string? AdminNotes, string? Reference, DateTime CreatedAt, DateTime? ProcessedAt);
public record AdminPo(int Id, string PoNumber, int VendorId, string VendorName, string? VendorWhatsApp, PurchaseOrderStatus Status, decimal ExpectedCost,
    string? VendorReference, string? Notes, DateTime? SentAt, DateTime? ExpectedDispatchDate);
public record AdminBillRef(int Id, string BillNumber, DateTime BillDate, decimal TotalAmount, decimal TaxableAmount, decimal AmountPaid, BillPaymentStatus PaymentStatus, string? FileUrl);
public record AdminInvoiceRef(int Id, string InvoiceNumber, DateTime InvoiceDate, decimal Total, bool IsCancelled, bool IsTaxInvoice);
public record AdminOrderDetail(
    int Id, string OrderNumber, DateTime CreatedAt, OrderStatus Status, PaymentStatus PaymentStatus, PaymentMethod PaymentMethod,
    int UserId, string CustomerName, string CustomerEmail, string? CustomerPhone,
    string ShipName, string ShipPhone, string? ShipEmail, string ShipLine1, string? ShipLine2, string? ShipLandmark, string ShipCity, string ShipState, string ShipPincode,
    string? CustomerGstin, decimal SubTotal, decimal Discount, string? CouponCode, decimal ShippingFee, decimal CodFee, decimal Total, decimal TaxAmount,
    decimal AmountPaid, decimal EstimatedCost, decimal ActualCost, bool CostIsActual, decimal EstimatedProfit,
    string? CustomerNotes, string? AdminNotes, DateTime? ExpectedDeliveryDate, DateTime? DeliveredAt, string? TrackingNumber, string? CourierName,
    string? CancelReason, string? RazorpayOrderId,
    List<AdminOrderItem> Items, List<AdminHistory> History, List<AdminPayment> Payments, List<AdminRefund> Refunds,
    AdminPo? PurchaseOrder, List<AdminBillRef> VendorBills, List<AdminInvoiceRef> Invoices, List<OrderStatus> NextStatuses,
    string? PaymentProvider = null, decimal? UpiAmount = null, string? CustomerUtr = null,
    DateTime? OriginalDeliveryDate = null, string? DeliveryDateReason = null, DateTime? DeliveryDateChangedAt = null,
    SalesChannel Channel = SalesChannel.Website, string? ChannelReference = null, string? CreatedByStaff = null,
    double? ShipLatitude = null, double? ShipLongitude = null, string? MapUrl = null);
public record ManualQuoteRequest(List<ManualOrderLine> Items, decimal Discount, decimal? ShippingFee, string? Pincode);
public record DeliveryDateRequest(DateTime Date, string? Reason, bool NotifyCustomer = true);

public record StatusUpdate(OrderStatus Status, string? Note, bool VisibleToCustomer, string? TrackingNumber, string? CourierName,
    DateTime? ExpectedDeliveryDate, bool CollectCod);
public record RecordPaymentRequest(PaymentMethod Method, decimal Amount, string? Reference, DateTime? PaidAt, string? Notes);
public record NoteRequest(string Note, bool VisibleToCustomer);
public record AdminNotesRequest(string? AdminNotes);
public record PurchaseOrderUpsert(int VendorId, PurchaseOrderStatus Status, decimal ExpectedCost, string? VendorReference, string? Notes, DateTime? ExpectedDispatchDate);
public record CancelRequest(string Reason);
public record InvoiceRequest(DateTime? InvoiceDate);

public class OrdersController(AppDbContext db, OrderService orders, InvoiceService invoices, EmailSender email) : AdminControllerBase
{
    [HttpGet]
    public async Task<PagedResult<AdminOrderRow>> List(string? search, OrderStatus? status, PaymentStatus? paymentStatus, PaymentMethod? paymentMethod,
        DateTime? from, DateTime? to, string? view, SalesChannel? channel = null, int page = 1, int pageSize = 25)
    {
        var q = db.Orders.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(o => o.OrderNumber.Contains(search) || o.ShipName.Contains(search) || o.ShipPhone.Contains(search) || o.ShipEmail!.Contains(search));
        if (status is not null) q = q.Where(o => o.Status == status);
        if (paymentStatus is not null) q = q.Where(o => o.PaymentStatus == paymentStatus);
        if (paymentMethod is not null) q = q.Where(o => o.PaymentMethod == paymentMethod);
        if (channel is not null) q = q.Where(o => o.Channel == channel);
        if (from is not null) q = q.Where(o => o.CreatedAt >= from);
        if (to is not null) q = q.Where(o => o.CreatedAt < to.Value.AddDays(1));
        q = view switch
        {
            "action" => q.Where(o => o.Status == OrderStatus.Pending || o.Status == OrderStatus.Confirmed),
            "transit" => q.Where(o => o.Status == OrderStatus.PlacedWithVendor || o.Status == OrderStatus.Dispatched || o.Status == OrderStatus.OutForDelivery),
            "no-bill" => q.Where(o => o.Status >= OrderStatus.PlacedWithVendor && o.Status <= OrderStatus.Delivered && !db.VendorBills.Any(b => b.OrderId == o.Id)),
            "no-invoice" => q.Where(o => o.Status == OrderStatus.Delivered && !o.Invoices.Any(i => !i.IsCancelled)),
            "cod-due" => q.Where(o => o.Status == OrderStatus.Delivered && o.AmountPaid < o.Total),
            _ => q
        };
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(o => o.CreatedAt).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
            .Select(o => new AdminOrderRow(o.Id, o.OrderNumber, o.CreatedAt, o.UserId, o.ShipName, o.ShipPhone, o.ShipCity, o.ShipPincode,
                o.Status, o.PaymentStatus, o.PaymentMethod, o.Total, o.AmountPaid, o.EstimatedCost, o.Items.Sum(i => i.Quantity),
                o.PurchaseOrder != null, db.VendorBills.Any(b => b.OrderId == o.Id), o.Invoices.Any(i => !i.IsCancelled), o.Channel))
            .ToListAsync();
        return new PagedResult<AdminOrderRow> { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    [HttpGet("counts")]
    public async Task<object> Counts()
    {
        var groups = await db.Orders.GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        return groups.ToDictionary(g => g.Key.ToString(), g => g.Count);
    }

    [HttpGet("{id:int}")]
    public async Task<AdminOrderDetail> Get(int id)
    {
        await orders.CompleteGatewayDetailsAsync(id);
        var o = await db.Orders.AsNoTracking()
            .Include(x => x.User).Include(x => x.Items).Include(x => x.History).Include(x => x.Payments).Include(x => x.Refunds)
            .Include(x => x.PurchaseOrder).ThenInclude(p => p!.Vendor).Include(x => x.Invoices)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Order not found", 404);
        var bills = await db.VendorBills.AsNoTracking().Where(b => b.OrderId == id).ToListAsync();
        var gst = (await db.BusinessSettings.AsNoTracking().FirstAsync()).GstRegistered;
        var actual = bills.Sum(b => gst ? b.TaxableAmount + b.ShippingCharges : b.TotalAmount);
        var estimated = gst ? Money.R2(o.Items.Sum(i => i.UnitVendorCost * i.Quantity * 100 / (100 + i.GstRate))) : o.EstimatedCost;
        var cost = bills.Count > 0 ? actual : estimated;
        var po = o.PurchaseOrder;

        return new AdminOrderDetail(o.Id, o.OrderNumber, o.CreatedAt, o.Status, o.PaymentStatus, o.PaymentMethod,
            o.UserId, o.User!.FullName, OrderService.IsPlaceholderEmail(o.User.Email) ? "" : o.User.Email, o.User.Phone,
            o.ShipName, o.ShipPhone, o.ShipEmail, o.ShipLine1, o.ShipLine2, o.ShipLandmark, o.ShipCity, o.ShipState, o.ShipPincode,
            o.CustomerGstin, o.SubTotal, o.Discount, o.CouponCode, o.ShippingFee, o.CodFee, o.Total, o.TaxAmount,
            o.AmountPaid, o.EstimatedCost, actual, bills.Count > 0, o.Total - o.TaxAmount - cost,
            o.CustomerNotes, o.AdminNotes, o.ExpectedDeliveryDate, o.DeliveredAt, o.TrackingNumber, o.CourierName, o.CancelReason, o.RazorpayOrderId,
            o.Items.Select(i => new AdminOrderItem(i.Id, i.ProductId, i.VariantId, i.ProductName, i.VariantName, i.ImageUrl, i.Quantity, i.UnitMrp, i.UnitPrice,
                i.LineTotal, i.UnitVendorPrice, i.UnitVendorCost, i.GstRate, i.HsnCode, i.VendorProductId, i.VendorUrl)).ToList(),
            o.History.OrderByDescending(h => h.CreatedAt).Select(h => new AdminHistory(h.Id, h.Status, h.Note, h.VisibleToCustomer, h.CreatedBy, h.CreatedAt)).ToList(),
            o.Payments.OrderBy(p => p.PaidAt).Select(p => new AdminPayment(p.Id, p.Method, p.Amount, p.IsSuccessful, p.GatewayPaymentId, p.Reference, p.PaidAt, p.Notes)).ToList(),
            o.Refunds.Select(r => new AdminRefund(r.Id, r.Type, r.Status, r.Amount, r.Reason, r.CustomerComment, r.AdminNotes, r.Reference, r.CreatedAt, r.ProcessedAt)).ToList(),
            po is null ? null : new AdminPo(po.Id, po.PoNumber, po.VendorId, po.Vendor!.Name, po.Vendor.WhatsApp ?? po.Vendor.Phone, po.Status, po.ExpectedCost,
                po.VendorReference, po.Notes, po.SentAt, po.ExpectedDispatchDate),
            bills.Select(b => new AdminBillRef(b.Id, b.BillNumber, b.BillDate, b.TotalAmount, b.TaxableAmount, b.AmountPaid, b.PaymentStatus, b.FileUrl)).ToList(),
            o.Invoices.OrderByDescending(i => i.Id).Select(i => new AdminInvoiceRef(i.Id, i.InvoiceNumber, i.InvoiceDate, i.Total, i.IsCancelled, i.IsTaxInvoice)).ToList(),
            NextStatuses(o.Status), o.PaymentProvider, o.UpiAmount, o.CustomerUtr,
            o.OriginalDeliveryDate, o.DeliveryDateReason, o.DeliveryDateChangedAt, o.Channel, o.ChannelReference, o.CreatedByStaff,
            o.ShipLatitude, o.ShipLongitude, OrderService.MapLink(o));
    }

    // ---------- Manual orders (Instagram, Facebook, WhatsApp, phone, walk-in) ----------

    [HttpGet("customer-lookup")]
    public Task<List<CustomerLookup>> CustomerLookup(string q) => orders.FindCustomersAsync(q ?? "");

    [HttpPost("manual/quote")]
    public Task<ManualQuote> ManualQuote(ManualQuoteRequest r) => orders.QuoteManualAsync(r.Items, r.Discount, r.ShippingFee, r.Pincode);

    [HttpPost("manual")]
    public Task<ManualOrderResult> CreateManual(ManualOrderRequest r) => orders.CreateManualOrderAsync(r, UserName);

    /// <summary>Ready-to-send WhatsApp message for the customer (summary, delivery date, payment and tracking link).</summary>
    [HttpGet("{id:int}/customer-message")]
    public async Task<object> CustomerMessage(int id)
    {
        var o = await db.Orders.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Order not found", 404);
        var (text, link) = await orders.CustomerMessageAsync(o);
        return new { text, link, whatsAppUrl = OrderService.WhatsAppLink(o.ShipPhone, text) };
    }

    private static List<OrderStatus> NextStatuses(OrderStatus s) => s switch
    {
        OrderStatus.Pending => [OrderStatus.Confirmed, OrderStatus.Cancelled],
        OrderStatus.Confirmed => [OrderStatus.PlacedWithVendor, OrderStatus.Cancelled],
        OrderStatus.PlacedWithVendor => [OrderStatus.Dispatched, OrderStatus.Cancelled],
        OrderStatus.Dispatched => [OrderStatus.OutForDelivery, OrderStatus.Delivered],
        OrderStatus.OutForDelivery => [OrderStatus.Delivered],
        OrderStatus.Delivered => [OrderStatus.Returned],
        _ => []
    };

    [HttpPut("{id:int}/status")]
    public async Task<AdminOrderDetail> UpdateStatus(int id, StatusUpdate r)
    {
        var o = await db.Orders.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Order not found", 404);
        if (r.Status == o.Status) throw new AppException("Order already has this status.");
        if (r.Status == OrderStatus.Cancelled)
        {
            await orders.CancelAsync(o, r.Note ?? "Cancelled by WoodsyCart", UserName);
            return await Get(id);
        }

        o.Status = r.Status;
        if (r.TrackingNumber is not null) o.TrackingNumber = r.TrackingNumber;
        if (r.CourierName is not null) o.CourierName = r.CourierName;
        if (r.ExpectedDeliveryDate is not null) o.ExpectedDeliveryDate = r.ExpectedDeliveryDate;

        var note = r.Note;
        switch (r.Status)
        {
            case OrderStatus.PlacedWithVendor:
                await EnsurePurchaseOrderAsync(o, PurchaseOrderStatus.Sent);
                note ??= "Your order is being crafted and prepared for dispatch.";
                break;
            case OrderStatus.Dispatched:
                note ??= "Your order has been dispatched." + (o.TrackingNumber is null ? "" : $" Tracking: {o.CourierName} {o.TrackingNumber}");
                await db.PurchaseOrders.Where(p => p.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PurchaseOrderStatus.Dispatched));
                break;
            case OrderStatus.Delivered:
                o.DeliveredAt = DateTime.UtcNow;
                note ??= "Delivered. Thank you for shopping with WoodsyCart!";
                await db.PurchaseOrders.Where(p => p.OrderId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PurchaseOrderStatus.Delivered));
                foreach (var item in o.Items)
                    await db.Products.Where(p => p.Id == item.ProductId).ExecuteUpdateAsync(s => s.SetProperty(p => p.SoldCount, p => p.SoldCount + item.Quantity));
                if (r.CollectCod && o.AmountPaid < o.Total)
                {
                    var due = o.Total - o.AmountPaid;
                    db.Payments.Add(new Payment { OrderId = id, Method = PaymentMethod.CashOnDelivery, Amount = due, IsSuccessful = true, Notes = "Collected on delivery" });
                    o.AmountPaid = o.Total;
                    o.PaymentStatus = PaymentStatus.Paid;
                }
                break;
            case OrderStatus.Confirmed:
                note ??= "Your order has been confirmed.";
                break;
        }
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = id, Status = r.Status, Note = note, VisibleToCustomer = r.VisibleToCustomer, CreatedBy = UserName });
        await db.SaveChangesAsync();

        WoodsyCart.Infrastructure.Email.EmailAttachment? invoicePdf = null;
        if (r.Status == OrderStatus.Delivered)
        {
            await invoices.GenerateAsync(id);
            var inv = await db.CustomerInvoices.Where(i => i.OrderId == id && !i.IsCancelled).OrderByDescending(i => i.Id).FirstOrDefaultAsync();
            if (inv is not null) invoicePdf = new(inv.InvoiceNumber.Replace('/', '-') + ".pdf", await invoices.RenderPdfAsync(inv.Id));
        }
        if (r.VisibleToCustomer) await orders.NotifyStatusAsync(o, r.Status, note, invoicePdf);
        return await Get(id);
    }

    /// <summary>Change the expected delivery date (e.g. vendor delay); the reason is shown to the customer.</summary>
    [HttpPut("{id:int}/delivery-date")]
    public async Task<AdminOrderDetail> ChangeDeliveryDate(int id, DeliveryDateRequest r)
    {
        var o = await db.Orders.FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Order not found", 404);
        await orders.ChangeDeliveryDateAsync(o, r.Date, r.Reason, UserName, r.NotifyCustomer);
        return await Get(id);
    }

    private static string Humanize(OrderStatus s) => string.Concat(s.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));

    private async Task<PurchaseOrder> EnsurePurchaseOrderAsync(Order o, PurchaseOrderStatus status)
    {
        var po = await db.PurchaseOrders.FirstOrDefaultAsync(p => p.OrderId == o.Id);
        if (po is not null)
        {
            if (po.Status < status) po.Status = status;
            if (status == PurchaseOrderStatus.Sent) po.SentAt ??= DateTime.UtcNow;
            return po;
        }
        var productIds = o.Items.Select(i => i.ProductId).ToList();
        var vendorId = await db.Products.IgnoreQueryFilters().Where(p => productIds.Contains(p.Id) && p.VendorId != null).Select(p => p.VendorId).FirstOrDefaultAsync()
                       ?? await db.Vendors.Select(v => (int?)v.Id).FirstOrDefaultAsync()
                       ?? throw new AppException("Add a vendor first.");
        po = new PurchaseOrder
        {
            PoNumber = "PO-" + o.OrderNumber[2..], OrderId = o.Id, VendorId = vendorId, Status = status,
            ExpectedCost = o.Items.Sum(i => i.UnitVendorCost * i.Quantity),
            SentAt = status >= PurchaseOrderStatus.Sent ? DateTime.UtcNow : null
        };
        db.PurchaseOrders.Add(po);
        return po;
    }

    [HttpPut("{id:int}/purchase-order")]
    public async Task<AdminOrderDetail> UpsertPurchaseOrder(int id, PurchaseOrderUpsert r)
    {
        var o = await db.Orders.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Order not found", 404);
        var po = await EnsurePurchaseOrderAsync(o, r.Status);
        po.VendorId = r.VendorId; po.Status = r.Status; po.ExpectedCost = r.ExpectedCost;
        po.VendorReference = r.VendorReference; po.Notes = r.Notes; po.ExpectedDispatchDate = r.ExpectedDispatchDate;
        if (r.Status >= PurchaseOrderStatus.Sent) po.SentAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
        return await Get(id);
    }

    [HttpGet("{id:int}/purchase-order/pdf")]
    public async Task<IActionResult> PurchaseOrderPdf(int id)
    {
        var o = await db.Orders.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Order not found", 404);
        var po = await EnsurePurchaseOrderAsync(o, PurchaseOrderStatus.Draft);
        await db.SaveChangesAsync();
        return File(await invoices.RenderPurchaseOrderPdfAsync(po.Id), "application/pdf", $"{po.PoNumber}.pdf");
    }

    /// <summary>Ready-to-send WhatsApp message for the vendor with item links and the customer's delivery address.</summary>
    [HttpGet("{id:int}/vendor-message")]
    public async Task<object> VendorMessage(int id)
    {
        var o = await db.Orders.AsNoTracking().Include(x => x.Items).Include(x => x.PurchaseOrder).ThenInclude(p => p!.Vendor)
            .FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Order not found", 404);
        var vendor = o.PurchaseOrder?.Vendor ?? await db.Vendors.AsNoTracking().FirstAsync();
        var sb = new StringBuilder();
        sb.AppendLine($"*New order from WoodsyCart*");
        sb.AppendLine($"Ref: {o.PurchaseOrder?.PoNumber ?? o.OrderNumber}");
        sb.AppendLine();
        var n = 0;
        foreach (var i in o.Items)
        {
            var vn = i.VendorVariantName ?? i.VariantName;
            sb.AppendLine($"{++n}. {i.ProductName}{(vn is null ? "" : $" — {vn}")} × {i.Quantity}");
            var gstPart = i.UnitVendorCost > i.UnitVendorPrice && i.UnitVendorPrice > 0
                ? $"₹{i.UnitVendorPrice:N0} + {i.GstRate:0.#}% GST = ₹{i.UnitVendorCost:N0}"
                : $"₹{i.UnitVendorCost:N0}";
            sb.AppendLine($"   Your price: {gstPart}{(i.Quantity > 1 ? " each" : "")}{(i.VendorUrl is null ? "" : $"\n   {i.VendorUrl}")}");
        }
        sb.AppendLine();
        sb.AppendLine("*Deliver to:*");
        sb.AppendLine(o.ShipName);
        sb.AppendLine(o.ShipPhone);
        sb.AppendLine(string.Join(", ", new[] { o.ShipLine1, o.ShipLine2, o.ShipLandmark, o.ShipCity, o.ShipPincode }.Where(x => !string.IsNullOrWhiteSpace(x))));
        if (o.ShipLatitude is not null) sb.AppendLine($"Location: {OrderService.MapLink(o)}");
        sb.AppendLine();
        sb.AppendLine("Please confirm the dispatch date and share your bill. Kindly don't include any price tags in the package. Thank you!");
        var text = sb.ToString();
        var phone = new string((vendor.WhatsApp ?? vendor.Phone ?? "").Where(char.IsDigit).ToArray());
        if (phone.Length == 10) phone = "91" + phone;
        return new { text, whatsAppUrl = phone.Length > 0 ? $"https://wa.me/{phone}?text={Uri.EscapeDataString(text)}" : $"https://wa.me/?text={Uri.EscapeDataString(text)}" };
    }

    [HttpPost("{id:int}/payments")]
    public async Task<AdminOrderDetail> RecordPayment(int id, RecordPaymentRequest r)
    {
        var o = await db.Orders.FindAsync(id) ?? throw new AppException("Order not found", 404);
        if (r.Amount <= 0) throw new AppException("Amount must be positive.");
        db.Payments.Add(new Payment { OrderId = id, Method = r.Method, Amount = r.Amount, IsSuccessful = true, Reference = r.Reference, PaidAt = r.PaidAt ?? DateTime.UtcNow, Notes = r.Notes });
        o.AmountPaid += r.Amount;
        o.PaymentStatus = o.AmountPaid >= o.Total ? PaymentStatus.Paid : PaymentStatus.PartiallyPaid;
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = id, Status = o.Status, Note = $"Payment of ₹{r.Amount:N2} received ({r.Method})", VisibleToCustomer = false, CreatedBy = UserName });
        if (o.PaymentStatus == PaymentStatus.Paid && o.Status == OrderStatus.Pending)
        {
            o.Status = OrderStatus.Confirmed;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = id, Status = OrderStatus.Confirmed, Note = "Payment received — thank you!", CreatedBy = UserName });
        }
        await db.SaveChangesAsync();
        await orders.NotifyPaymentAsync(o, r.Amount, $"recorded by {UserName} ({r.Method}{(string.IsNullOrWhiteSpace(r.Reference) ? "" : $", ref {r.Reference}")})");
        return await Get(id);
    }

    [HttpPost("{id:int}/notes")]
    public async Task<AdminOrderDetail> AddNote(int id, NoteRequest r)
    {
        var o = await db.Orders.FindAsync(id) ?? throw new AppException("Order not found", 404);
        db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = id, Status = o.Status, Note = r.Note, VisibleToCustomer = r.VisibleToCustomer, CreatedBy = UserName });
        await db.SaveChangesAsync();
        if (r.VisibleToCustomer) await orders.NotifyNoteAsync(o, r.Note);
        return await Get(id);
    }

    [HttpPut("{id:int}/admin-notes")]
    public async Task<IActionResult> AdminNotes(int id, AdminNotesRequest r)
    {
        await db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.AdminNotes, r.AdminNotes));
        return NoContent();
    }

    [HttpPost("{id:int}/invoice")]
    public async Task<AdminOrderDetail> GenerateInvoice(int id, InvoiceRequest? r)
    {
        await invoices.GenerateAsync(id, r?.InvoiceDate);
        return await Get(id);
    }
}

public record RefundUpdate(RefundStatus Status, decimal Amount, string? AdminNotes, string? Reference);
public record RefundRow(int Id, int OrderId, string OrderNumber, string Customer, RefundType Type, RefundStatus Status, decimal Amount, string Reason,
    string? CustomerComment, string? AdminNotes, string? Reference, DateTime CreatedAt, DateTime? ProcessedAt, PaymentMethod PaymentMethod);
public record CreateRefund(int OrderId, RefundType Type, decimal Amount, string Reason);

public class RefundsController(AppDbContext db, OrderService orders) : AdminControllerBase
{
    [HttpGet]
    public Task<List<RefundRow>> List(RefundStatus? status) => db.Refunds.AsNoTracking()
        .Where(r => status == null || r.Status == status).OrderByDescending(r => r.CreatedAt)
        .Select(r => new RefundRow(r.Id, r.OrderId, r.Order!.OrderNumber, r.Order.ShipName, r.Type, r.Status, r.Amount, r.Reason, r.CustomerComment,
            r.AdminNotes, r.Reference, r.CreatedAt, r.ProcessedAt, r.Order.PaymentMethod)).ToListAsync();

    [HttpPost]
    public async Task<IActionResult> Create(CreateRefund r)
    {
        if (!await db.Orders.AnyAsync(o => o.Id == r.OrderId)) throw new AppException("Order not found", 404);
        db.Refunds.Add(new Refund { OrderId = r.OrderId, Type = r.Type, Amount = r.Amount, Reason = r.Reason, Status = RefundStatus.Approved });
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, RefundUpdate r)
    {
        var refund = await db.Refunds.Include(x => x.Order).FirstOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Refund not found", 404);
        refund.Status = r.Status; refund.Amount = r.Amount; refund.AdminNotes = r.AdminNotes; refund.Reference = r.Reference;
        var o = refund.Order!;
        if (r.Status == RefundStatus.Processed)
        {
            refund.ProcessedAt = DateTime.UtcNow;
            o.PaymentStatus = r.Amount >= o.AmountPaid ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
            if (refund.Type == RefundType.Return) o.Status = OrderStatus.Returned;
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = o.Id, Status = o.Status, Note = $"Refund of ₹{r.Amount:N2} processed.", CreatedBy = UserName });
            await orders.NotifyRefundAsync(o, r.Amount, r.Reference);
        }
        else if (r.Status == RefundStatus.Approved && refund.Type == RefundType.Return)
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = o.Id, Status = o.Status, Note = "Return approved — pickup will be arranged.", CreatedBy = UserName });
        else if (r.Status == RefundStatus.Rejected)
            db.OrderStatusHistory.Add(new OrderStatusHistory { OrderId = o.Id, Status = o.Status, Note = $"Return request declined. {r.AdminNotes}", CreatedBy = UserName });
        await db.SaveChangesAsync();
        return NoContent();
    }
}

public record InvoiceRow(int Id, string InvoiceNumber, DateTime InvoiceDate, int OrderId, string OrderNumber, string BillToName, decimal TaxableAmount,
    decimal Cgst, decimal Sgst, decimal Igst, decimal Total, bool IsCancelled, bool IsTaxInvoice);

public class InvoicesController(AppDbContext db, InvoiceService invoices) : AdminControllerBase
{
    [HttpGet]
    public async Task<PagedResult<InvoiceRow>> List(string? search, DateTime? from, DateTime? to, int page = 1, int pageSize = 25)
    {
        var q = db.CustomerInvoices.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) q = q.Where(i => i.InvoiceNumber.Contains(search) || i.BillToName.Contains(search) || i.Order!.OrderNumber.Contains(search));
        if (from is not null) q = q.Where(i => i.InvoiceDate >= from);
        if (to is not null) q = q.Where(i => i.InvoiceDate < to.Value.AddDays(1));
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(i => i.Id).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize)
            .Select(i => new InvoiceRow(i.Id, i.InvoiceNumber, i.InvoiceDate, i.OrderId, i.Order!.OrderNumber, i.BillToName, i.TaxableAmount, i.Cgst, i.Sgst, i.Igst, i.Total, i.IsCancelled, i.IsTaxInvoice))
            .ToListAsync();
        return new PagedResult<InvoiceRow> { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> Pdf(int id)
    {
        var inv = await db.CustomerInvoices.FindAsync(id) ?? throw new AppException("Invoice not found", 404);
        return File(await invoices.RenderPdfAsync(id), "application/pdf", $"{inv.InvoiceNumber.Replace('/', '-')}.pdf");
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        await db.CustomerInvoices.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.IsCancelled, true));
        return NoContent();
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export(DateTime? from, DateTime? to)
    {
        var q = db.CustomerInvoices.AsNoTracking().Where(i => !i.IsCancelled);
        if (from is not null) q = q.Where(i => i.InvoiceDate >= from);
        if (to is not null) q = q.Where(i => i.InvoiceDate < to.Value.AddDays(1));
        var rows = await q.OrderBy(i => i.InvoiceDate).Select(i => new { i.InvoiceNumber, i.InvoiceDate, i.BillToName, i.BillToGstin, i.PlaceOfSupply, i.TaxableAmount, i.Cgst, i.Sgst, i.Igst, i.Total }).ToListAsync();
        var sb = new StringBuilder("Invoice No,Date,Customer,GSTIN,Place of Supply,Taxable,CGST,SGST,IGST,Total\n");
        foreach (var r in rows)
            sb.AppendLine($"{r.InvoiceNumber},{FinancialYear.ToIst(r.InvoiceDate):dd-MM-yyyy},\"{r.BillToName}\",{r.BillToGstin},{r.PlaceOfSupply},{r.TaxableAmount},{r.Cgst},{r.Sgst},{r.Igst},{r.Total}");
        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "sales-register.csv");
    }
}
