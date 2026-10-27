namespace WoodsyCart.Domain;

public enum UserRole { Customer = 0, Staff = 1, Admin = 2 }

public enum ProductSource { Vendor = 0, Manual = 1 }

public enum MediaType { Image = 1, Video = 3 }

public enum PriceRounding { None = 0, Nearest10 = 1, EndWith99 = 2, Nearest100 = 3 }

public enum OrderStatus
{
    Pending = 0,            // placed, awaiting payment / confirmation
    Confirmed = 1,          // confirmed by WoodsyCart
    PlacedWithVendor = 2,   // purchase order sent to vendor
    Dispatched = 3,         // vendor shipped
    OutForDelivery = 4,
    Delivered = 5,
    Cancelled = 6,
    Returned = 7
}

public enum PaymentStatus { Pending = 0, PartiallyPaid = 1, Paid = 2, Failed = 3, Refunded = 4, PartiallyRefunded = 5 }

/// <summary>How the customer pays. "Razorpay" (1) is kept for older orders and means "online via gateway".</summary>
public enum PaymentMethod { CashOnDelivery = 0, Razorpay = 1, BankTransfer = 2, Upi = 3, Card = 4, NetBanking = 5, Wallet = 6, Emi = 7 }

public enum RefundStatus { Requested = 0, Approved = 1, Rejected = 2, Processed = 3 }

public enum RefundType { Cancellation = 0, Return = 1, Partial = 2 }

public enum PurchaseOrderStatus { Draft = 0, Sent = 1, Accepted = 2, Dispatched = 3, Delivered = 4, Cancelled = 5 }

public enum BillPaymentStatus { Unpaid = 0, PartiallyPaid = 1, Paid = 2 }

public enum DiscountType { Percent = 0, Flat = 1 }

public enum OfferScope { AllProducts = 0, Categories = 1, Products = 2 }

/// <summary>Where an order came from. Website orders are placed by customers; the rest are entered in Admin.</summary>
public enum SalesChannel { Website = 0, Instagram = 1, Facebook = 2, WhatsApp = 3, Phone = 4, WalkIn = 5, Other = 6 }

public enum EnquiryStatus { New = 0, InProgress = 1, Closed = 2 }

public enum SyncStatus { Running = 0, Completed = 1, Failed = 2 }

/// <summary>Vendor-mark clean-up state of a product photo.</summary>
public enum ImageCleanStatus
{
    NotProcessed = 0,   // waiting for the image cleaner
    Clean = 1,          // no vendor marks found (may still have been tidied)
    Cleaned = 2,        // vendor name / price removed automatically
    NeedsReview = 3,    // unclear text found — check before/after in Admin
    Failed = 4,
    Restored = 5,       // admin chose the original photo
    Approved = 6        // admin checked the cleaned photo
}

/// <summary>White-background replacement state of a product photo.</summary>
public enum BackgroundStatus
{
    None = 0,
    Marked = 1,         // admin marked it (showroom/factory background) for the next batch run
    Queued = 2,         // waiting for the image cleaner
    Ready = 3,          // white-background version made — check it in Admin before it goes on the store
    Applied = 4,        // shown on the store
    Discarded = 5,      // admin kept the photo as it was
    Failed = 6
}

public enum SocialPlatform { Instagram = 0, Facebook = 1, Threads = 2, InstagramReel = 3, FacebookReel = 4 }

public enum SocialPostStatus
{
    Queued = 0,         // waiting for its time (ScheduledAt)
    Publishing = 1,
    Published = 2,      // live and matches the product
    OutOfDate = 3,      // live, but the product changed (Instagram: copy the new caption or replace the post)
    Failed = 4,
    Removed = 5         // deleted from the platform or taken out of the queue
}

public enum ExpenseCategory
{
    Marketing = 0, Advertising = 1, Packaging = 2, Delivery = 3, PaymentGatewayFees = 4,
    SoftwareHosting = 5, Salary = 6, Rent = 7, Utilities = 8, Legal = 9, Miscellaneous = 10
}
