namespace WoodsyCart.Infrastructure.Email;

/// <summary>Every email the app sends. Each can be switched off in Admin → Email &amp; notifications.</summary>
public enum EmailEvent
{
    // To the customer
    OrderPlaced, PaymentReceived, OrderConfirmed, OrderWithVendor, OrderDispatched, OrderOutForDelivery, OrderDelivered,
    DeliveryDateChanged, OrderCancelled, ReturnRequested, RefundProcessed, OrderNote, AccountCreated, EnquiryReceived,
    // To the admin team
    AdminNewOrder, AdminPaymentReceived, AdminUtrSubmitted, AdminUnmatchedPayment, AdminOrderCancelled, AdminReturnRequested,
    AdminNewEnquiry, AdminNewReview, AdminVendorSync, AdminSocialProblem, AdminSocialComments,
    // Always sent (not switchable)
    Test, PasswordReset, PasswordChanged
}

public record EmailEventInfo(string Key, string Label, string Audience, string Description);

public static class EmailEvents
{
    public static readonly EmailEventInfo[] All =
    [
        new(nameof(EmailEvent.OrderPlaced), "Order placed", "Customer", "Order summary with items, total, delivery date and a link to view, pay and track the order."),
        new(nameof(EmailEvent.PaymentReceived), "Payment received", "Customer", "When a UPI / bank / gateway payment or a payment you record completes the order's payment."),
        new(nameof(EmailEvent.OrderConfirmed), "Order confirmed", "Customer", "You mark the order Confirmed."),
        new(nameof(EmailEvent.OrderWithVendor), "Being made", "Customer", "You mark the order Placed with vendor."),
        new(nameof(EmailEvent.OrderDispatched), "Dispatched", "Customer", "With courier and tracking number when given."),
        new(nameof(EmailEvent.OrderOutForDelivery), "Out for delivery", "Customer", "You mark the order Out for delivery."),
        new(nameof(EmailEvent.OrderDelivered), "Delivered (with invoice)", "Customer", "Thank-you email with the invoice PDF attached."),
        new(nameof(EmailEvent.DeliveryDateChanged), "Delivery date changed", "Customer", "New date and reason (sent when \"Email the customer\" is ticked)."),
        new(nameof(EmailEvent.OrderCancelled), "Order cancelled", "Customer", "Cancellation with reason and refund information."),
        new(nameof(EmailEvent.ReturnRequested), "Return request received", "Customer", "Confirms we received their return request."),
        new(nameof(EmailEvent.RefundProcessed), "Refund processed", "Customer", "Amount and reference of the refund."),
        new(nameof(EmailEvent.OrderNote), "Order update note", "Customer", "A note you add to the order with \"Visible to customer\" ticked."),
        new(nameof(EmailEvent.AccountCreated), "Welcome", "Customer", "After the customer creates an account."),
        new(nameof(EmailEvent.EnquiryReceived), "Enquiry received", "Customer", "Confirms their contact-form message (when they gave an email)."),
        new(nameof(EmailEvent.AdminNewOrder), "New order", "Admin", "Every new order — website and manual — with items, customer and payment."),
        new(nameof(EmailEvent.AdminPaymentReceived), "Payment received", "Admin", "A customer payment was received (gateway, UPI alert or recorded)."),
        new(nameof(EmailEvent.AdminUtrSubmitted), "UPI reference to check", "Admin", "A customer entered a UPI reference that hasn't been matched to a bank alert yet."),
        new(nameof(EmailEvent.AdminUnmatchedPayment), "Unmatched payment", "Admin", "Money arrived (bank / UPI alert) but no order could be matched."),
        new(nameof(EmailEvent.AdminOrderCancelled), "Cancelled by customer", "Admin", "A customer cancelled their order."),
        new(nameof(EmailEvent.AdminReturnRequested), "Return requested", "Admin", "A customer asked to return an order."),
        new(nameof(EmailEvent.AdminNewEnquiry), "New enquiry", "Admin", "Contact form and product questions."),
        new(nameof(EmailEvent.AdminNewReview), "Review to approve", "Admin", "A customer wrote a review."),
        new(nameof(EmailEvent.AdminVendorSync), "Vendor sync report", "Admin", "When a sync fails, or finds price changes or new products."),
        new(nameof(EmailEvent.AdminSocialProblem), "Social media problem", "Admin", "An Instagram or Facebook post failed, or the Meta connection stopped working."),
        new(nameof(EmailEvent.AdminSocialComments), "New social media comments", "Admin", "Someone commented on an Instagram, Facebook or Threads post (at most one email every 30 minutes).")
    ];
}
