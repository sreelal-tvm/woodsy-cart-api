using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Email;
using static WoodsyCart.Infrastructure.Email.EmailTemplates;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>Order emails to the customer and to the admin team (each can be switched off in Admin → Email &amp; notifications).</summary>
public partial class OrderService
{
    private async Task<List<OrderItem>> ItemsOf(Order o) =>
        o.Items.Count > 0 ? o.Items.ToList() : await db.OrderItems.AsNoTracking().Where(i => i.OrderId == o.Id).ToListAsync();

    private static string FirstName(Order o) => E(o.ShipName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? o.ShipName);

    private async Task NotifyOrderPlacedAsync(Order order)
    {
        var items = await ItemsOf(order);
        var paid = order.PaymentStatus == PaymentStatus.Paid;
        var payLine = paid ? Info("✅ Payment received — thank you!", "#ecfdf5", "#10b981")
            : order.PaymentMethod == PaymentMethod.CashOnDelivery ? Info($"Please keep <b>{Rs(order.Total - order.AmountPaid)}</b> ready at delivery (cash on delivery).")
            : order.AmountPaid > 0 ? Info($"{Rs(order.AmountPaid)} received. Balance <b>{Rs(order.Total - order.AmountPaid)}</b> — pay from your order page.")
            : Info("Your order is reserved. Please complete the payment from your order page so we can start making it.", "#fff7ed", "#f97316");
        await email.CustomerAsync(EmailEvent.OrderPlaced, order.ShipEmail, $"Order {order.OrderNumber} received — thank you!",
            "Thank you for your order!",
            $"<p>Hi {FirstName(order)}, we've received your order <b>{order.OrderNumber}</b>.</p>{payLine}{OrderTable(order, items)}{Delivery(order)}{Address(order)}" +
            "<p>We'll email you when it's on its way.</p>",
            (paid ? "View or track your order" : "View and pay for your order", email.OrderLink(order)), orderId: order.Id);

        await email.AdminsAsync(EmailEvent.AdminNewOrder, $"New order {order.OrderNumber} — {Rs(order.Total)} ({ChannelName(order.Channel)})",
            $"New order {order.OrderNumber}",
            $"<p><b>{E(order.ShipName)}</b> · {E(order.ShipPhone)}{(string.IsNullOrEmpty(RealEmail(order.ShipEmail)) ? "" : " · " + E(order.ShipEmail))}<br/>{E(order.ShipCity)} {E(order.ShipPincode)}</p>" +
            $"<p>Channel: <b>{ChannelName(order.Channel)}</b>{(order.ChannelReference is null ? "" : " · " + E(order.ChannelReference))}{(order.CreatedByStaff is null ? "" : " · entered by " + E(order.CreatedByStaff))}<br/>" +
            $"Payment: <b>{PaymentLabel(order)}</b> · {(paid ? "paid" : order.AmountPaid > 0 ? $"{Rs(order.AmountPaid)} paid" : "not paid yet")}</p>" +
            OrderTable(order, items) + (string.IsNullOrWhiteSpace(order.CustomerNotes) ? "" : Info("Customer note: " + E(order.CustomerNotes))),
            ("Open the order", email.AdminOrderLink(order)), order.Id);
    }

    /// <summary>A payment was received (gateway, UPI/bank alert, or recorded by the team).</summary>
    public async Task NotifyPaymentAsync(Order order, decimal amount, string how, bool toCustomer = true)
    {
        var full = order.PaymentStatus == PaymentStatus.Paid;
        if (full && toCustomer)
            await email.CustomerAsync(EmailEvent.PaymentReceived, order.ShipEmail, $"Payment received for order {order.OrderNumber}",
                "Payment received — thank you!",
                $"<p>Hi {FirstName(order)}, we've received your payment of <b>{Rs(order.AmountPaid)}</b> for order <b>{order.OrderNumber}</b>.</p>" +
                "<p>Your furniture is now being prepared. We'll keep you posted.</p>" + Delivery(order),
                ("View your order", email.OrderLink(order)), orderId: order.Id);
        await email.AdminsAsync(EmailEvent.AdminPaymentReceived, $"Payment {Rs(amount)} received — {order.OrderNumber}",
            $"{Rs(amount)} received",
            $"<p>Order <b>{order.OrderNumber}</b> ({E(order.ShipName)}) — {E(how)}.</p><p>{(full ? "The order is now <b>fully paid</b>." : $"Paid so far {Rs(order.AmountPaid)} of {Rs(order.Total)}.")}</p>",
            ("Open the order", email.AdminOrderLink(order)), order.Id);
    }

    /// <summary>Status update to the customer (when "Visible to customer" is ticked). Delivered emails carry the invoice PDF.</summary>
    public async Task NotifyStatusAsync(Order o, OrderStatus status, string? note, EmailAttachment? invoice = null)
    {
        var (ev, subject, heading) = status switch
        {
            OrderStatus.Confirmed => (EmailEvent.OrderConfirmed, $"Order {o.OrderNumber} confirmed", "Your order is confirmed"),
            OrderStatus.PlacedWithVendor => (EmailEvent.OrderWithVendor, $"Order {o.OrderNumber} is being made", "Your furniture is being made"),
            OrderStatus.Dispatched => (EmailEvent.OrderDispatched, $"Order {o.OrderNumber} is on its way", "Your order is on its way 🚚"),
            OrderStatus.OutForDelivery => (EmailEvent.OrderOutForDelivery, $"Order {o.OrderNumber} is out for delivery", "Out for delivery today"),
            OrderStatus.Delivered => (EmailEvent.OrderDelivered, $"Order {o.OrderNumber} delivered — thank you!", "Delivered — enjoy your new furniture!"),
            _ => (EmailEvent.OrderNote, $"Update on order {o.OrderNumber}", "An update on your order")
        };
        var body = $"<p>Hi {FirstName(o)},</p>" + (string.IsNullOrWhiteSpace(note) ? "" : $"<p>{E(note)}</p>");
        if (status == OrderStatus.Dispatched && o.TrackingNumber is not null) body += Info($"Courier: <b>{E(o.CourierName ?? "Our delivery partner")}</b><br/>Tracking no.: <b>{E(o.TrackingNumber)}</b>");
        if (status is OrderStatus.Dispatched or OrderStatus.PlacedWithVendor or OrderStatus.Confirmed) body += Delivery(o);
        if (status == OrderStatus.OutForDelivery) body += Address(o) + (o.Total - o.AmountPaid > 0 && o.PaymentMethod == PaymentMethod.CashOnDelivery ? Info($"Please keep <b>{Rs(o.Total - o.AmountPaid)}</b> ready.") : "");
        if (status == OrderStatus.Delivered)
            body += (invoice is null ? "" : "<p>Your invoice is attached.</p>") +
                    "<p>How do you like it? <b>Rate your purchase</b> — it takes a minute and helps other families choose.</p>" +
                    $"<p style=\"color:#6b7280\">Returns are accepted within {ReturnWindowDays} days of delivery from your order page.</p>";
        var button = status == OrderStatus.Delivered ? ("★ Rate & review your purchase", email.OrderLink(o) + "#review") : ("View your order", email.OrderLink(o));
        await email.CustomerAsync(ev, o.ShipEmail, subject, heading, body, button, invoice, o.Id);
    }

    /// <summary>A note added to the order with "Visible to customer" ticked.</summary>
    public Task NotifyNoteAsync(Order o, string note) =>
        email.CustomerAsync(EmailEvent.OrderNote, o.ShipEmail, $"Update on order {o.OrderNumber}", "An update on your order",
            $"<p>Hi {FirstName(o)},</p><p>{E(note)}</p>", ("View your order", email.OrderLink(o)), orderId: o.Id);

    private Task NotifyDeliveryDateAsync(Order o, DateTime? old, DateTime date, string? reason, bool later)
    {
        var when = date.ToString("dddd, d MMMM");
        var body = later && old is not null
            ? $"<p>Hi {FirstName(o)},</p><p>We're sorry — the delivery of your order <b>{o.OrderNumber}</b> will be a little later than planned.</p>" +
              Info($"New expected delivery: <b>{when}</b><br/><span style=\"color:#6b7280\">Was {old:dddd, d MMMM}</span>", "#fff7ed", "#f97316") +
              (reason is null ? "" : $"<p><b>Reason:</b> {E(reason)}</p>") + "<p>Thank you for your patience. Reply to this email or WhatsApp us if you have any questions.</p>"
            : $"<p>Hi {FirstName(o)},</p><p>Your order <b>{o.OrderNumber}</b> is now expected on <b>{when}</b>.</p>" + (reason is null ? "" : $"<p>{E(reason)}</p>");
        return email.CustomerAsync(EmailEvent.DeliveryDateChanged, o.ShipEmail,
            later && old is not null ? $"Order {o.OrderNumber}: new delivery date {date:d MMM}" : $"Order {o.OrderNumber}: delivery on {date:d MMM}",
            later && old is not null ? "A new delivery date for your order" : "Your delivery date", body, ("View your order", email.OrderLink(o)), orderId: o.Id);
    }

    private async Task NotifyCancelledAsync(Order o, string reason, bool byCustomer)
    {
        await email.CustomerAsync(EmailEvent.OrderCancelled, o.ShipEmail, $"Order {o.OrderNumber} cancelled", "Your order has been cancelled",
            $"<p>Hi {FirstName(o)}, your order <b>{o.OrderNumber}</b> has been cancelled.</p>" + (string.IsNullOrWhiteSpace(reason) ? "" : $"<p>Reason: {E(reason)}</p>") +
            (o.AmountPaid > 0 ? Info($"Your refund of <b>{Rs(o.AmountPaid)}</b> will be processed within 5–7 working days.") : ""),
            ("View your order", email.OrderLink(o)), orderId: o.Id);
        if (byCustomer)
            await email.AdminsAsync(EmailEvent.AdminOrderCancelled, $"Order {o.OrderNumber} cancelled by the customer", "Order cancelled by the customer",
                $"<p><b>{E(o.ShipName)}</b> ({E(o.ShipPhone)}) cancelled order <b>{o.OrderNumber}</b> ({Rs(o.Total)}).</p><p>Reason: {E(reason)}</p>" +
                (o.AmountPaid > 0 ? Info($"They had paid {Rs(o.AmountPaid)} — a refund was created in Returns &amp; refunds.") : "") +
                (o.Status >= OrderStatus.PlacedWithVendor ? "" : "<p>If you already asked the vendor to make it, let them know.</p>"),
                ("Open the order", email.AdminOrderLink(o)), o.Id);
    }

    private async Task NotifyReturnRequestedAsync(Order o, string reason, string? comment)
    {
        await email.CustomerAsync(EmailEvent.ReturnRequested, o.ShipEmail, $"We've received your return request for {o.OrderNumber}", "Return request received",
            $"<p>Hi {FirstName(o)}, we've received your request to return order <b>{o.OrderNumber}</b>.</p><p>Reason: {E(reason)}</p>" +
            "<p>Our team will contact you within 1–2 working days to arrange the pickup.</p>", ("View your order", email.OrderLink(o)), orderId: o.Id);
        await email.AdminsAsync(EmailEvent.AdminReturnRequested, $"Return requested for {o.OrderNumber}", "Return requested",
            $"<p><b>{E(o.ShipName)}</b> ({E(o.ShipPhone)}) wants to return order <b>{o.OrderNumber}</b> ({Rs(o.Total)}).</p><p>Reason: {E(reason)}</p>" +
            (string.IsNullOrWhiteSpace(comment) ? "" : Info(E(comment))), ("Review in Returns & refunds", $"{email.AdminUrl}/refunds"), o.Id);
    }

    /// <summary>Refund marked processed.</summary>
    public Task NotifyRefundAsync(Order o, decimal amount, string? reference) =>
        email.CustomerAsync(EmailEvent.RefundProcessed, o.ShipEmail, $"Refund processed for {o.OrderNumber}", "Your refund is on its way",
            $"<p>Hi {FirstName(o)}, we've refunded <b>{Rs(amount)}</b> for order <b>{o.OrderNumber}</b>.</p>" +
            (string.IsNullOrWhiteSpace(reference) ? "" : $"<p>Reference: <b>{E(reference)}</b></p>") +
            "<p>It usually shows in your account within 5–7 working days, depending on your bank.</p>", ("View your order", email.OrderLink(o)), orderId: o.Id);

    private Task NotifyUtrSubmittedAsync(Order o, string utr) =>
        email.AdminsAsync(EmailEvent.AdminUtrSubmitted, $"Check UPI payment for {o.OrderNumber}", "A customer says they've paid",
            $"<p><b>{E(o.ShipName)}</b> entered UPI reference <b>{E(utr)}</b> for order <b>{o.OrderNumber}</b> ({Rs((o.UpiAmount ?? o.Total) - o.AmountPaid)}).</p>" +
            "<p>No matching bank alert has arrived yet. Check your bank app and confirm the payment.</p>",
            ("Check in UPI verification", $"{email.AdminUrl}/payments"), o.Id);
}
