using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

public class Order : BaseEntity
{
    public string OrderNumber { get; set; } = "";
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;
    public PaymentMethod PaymentMethod { get; set; }
    public SalesChannel Channel { get; set; } = SalesChannel.Website;
    /// <summary>Where on the channel, e.g. the Instagram handle or chat the order came from.</summary>
    public string? ChannelReference { get; set; }
    /// <summary>Team member who entered a manual order (null for website orders).</summary>
    public string? CreatedByStaff { get; set; }

    // Shipping address snapshot
    public string ShipName { get; set; } = "";
    public string ShipPhone { get; set; } = "";
    public string? ShipEmail { get; set; }
    public string ShipLine1 { get; set; } = "";
    public string? ShipLine2 { get; set; }
    public string? ShipLandmark { get; set; }
    public string ShipCity { get; set; } = "";
    public string ShipState { get; set; } = "";
    public string ShipPincode { get; set; } = "";
    /// <summary>Delivery point from the map picker (optional) — shared with the vendor as a Google Maps link.</summary>
    public double? ShipLatitude { get; set; }
    public double? ShipLongitude { get; set; }
    public string? CustomerGstin { get; set; }

    public decimal SubTotal { get; set; }        // sum of line totals (GST inclusive)
    public decimal Discount { get; set; }
    public string? CouponCode { get; set; }
    /// <summary>Total saved through festival offers (already reflected in SubTotal; for reporting).</summary>
    public decimal OfferDiscount { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal CodFee { get; set; }
    public decimal Total { get; set; }
    public decimal TaxAmount { get; set; }        // GST included in Total
    public decimal AmountPaid { get; set; }
    public decimal EstimatedCost { get; set; }    // vendor cost snapshot at order time

    public string? CustomerNotes { get; set; }
    public string? AdminNotes { get; set; }
    public DateTime? ExpectedDeliveryDate { get; set; }
    /// <summary>The first delivery date promised, kept when the date is changed later.</summary>
    public DateTime? OriginalDeliveryDate { get; set; }
    /// <summary>Why the delivery date last changed (shown to the customer).</summary>
    public string? DeliveryDateReason { get; set; }
    public DateTime? DeliveryDateChangedAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
    public string? TrackingNumber { get; set; }
    public string? CourierName { get; set; }

    public string? RazorpayOrderId { get; set; }
    /// <summary>PhonePe / Cashfree / PayU / Paytm: our id for the latest payment attempt (order number + suffix).</summary>
    public string? GatewayReference { get; set; }
    /// <summary>Provider that processes this order's payment (see PaymentProviders).</summary>
    public string? PaymentProvider { get; set; }
    /// <summary>Direct UPI: the exact amount the customer is asked to pay (total minus a few "fingerprint" paise).</summary>
    public decimal? UpiAmount { get; set; }
    /// <summary>12-digit UPI/bank reference the customer entered after paying.</summary>
    public string? CustomerUtr { get; set; }
    /// <summary>Secret for viewing/paying the order without signing in (guest checkout links).</summary>
    public string AccessToken { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>App notification reminders already sent for this order.</summary>
    public DateTime? PaymentReminderAt { get; set; }
    public DateTime? ReviewReminderAt { get; set; }

    public ICollection<OrderItem> Items { get; set; } = [];
    public ICollection<OrderStatusHistory> History { get; set; } = [];
    public ICollection<Payment> Payments { get; set; } = [];
    public ICollection<Refund> Refunds { get; set; } = [];
    public PurchaseOrder? PurchaseOrder { get; set; }
    public ICollection<CustomerInvoice> Invoices { get; set; } = [];
}

public class OrderItem : BaseEntity
{
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int? VariantId { get; set; }
    public string ProductName { get; set; } = "";
    public string? VariantName { get; set; }
    /// <summary>The vendor's name for what to make (differs from VariantName when options were renamed or added in Admin).</summary>
    public string? VendorVariantName { get; set; }
    public string? ImageUrl { get; set; }
    public string? HsnCode { get; set; }
    public decimal GstRate { get; set; }
    public int Quantity { get; set; }
    public decimal UnitMrp { get; set; }
    public decimal UnitPrice { get; set; }
    /// <summary>Price before any offer (equals UnitPrice when no offer applied).</summary>
    public decimal UnitRegularPrice { get; set; }
    public int? OfferId { get; set; }
    public string? OfferName { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>Vendor's listed unit price at order time (before GST when the vendor adds GST on top).</summary>
    public decimal UnitVendorPrice { get; set; }
    /// <summary>What we actually pay the vendor per unit, including GST.</summary>
    public decimal UnitVendorCost { get; set; }
    public long? VendorProductId { get; set; }
    public string? VendorUrl { get; set; }
}

public class OrderStatusHistory : BaseEntity
{
    public int OrderId { get; set; }
    public OrderStatus Status { get; set; }
    public string? Note { get; set; }
    public bool VisibleToCustomer { get; set; } = true;
    public string? CreatedBy { get; set; }
}

public class Payment : BaseEntity
{
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }
    public bool IsSuccessful { get; set; }
    public string? GatewayOrderId { get; set; }
    public string? GatewayPaymentId { get; set; }
    public string? Reference { get; set; }
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
    public string? Notes { get; set; }
    /// <summary>Gateway's own fee for this payment, incl. GST (null = not known yet). Also booked as an expense.</summary>
    public decimal? GatewayFee { get; set; }
    /// <summary>GST part of GatewayFee, as reported by the gateway.</summary>
    public decimal? GatewayFeeTax { get; set; }
}

public class Refund : BaseEntity
{
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public RefundType Type { get; set; }
    public RefundStatus Status { get; set; } = RefundStatus.Requested;
    public decimal Amount { get; set; }
    public string Reason { get; set; } = "";
    public string? CustomerComment { get; set; }
    public string? AdminNotes { get; set; }
    public string? Reference { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public class Coupon : BaseEntity
{
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public DiscountType DiscountType { get; set; }
    public decimal Value { get; set; }
    public decimal? MaxDiscount { get; set; }
    public decimal MinOrderAmount { get; set; }
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public int? UsageLimit { get; set; }
    public int UsedCount { get; set; }
    public int? PerUserLimit { get; set; }
    public bool IsActive { get; set; } = true;
}
