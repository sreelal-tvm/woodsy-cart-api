using WoodsyCart.Infrastructure.Email;

namespace WoodsyCart.Infrastructure.Push;

/// <summary>An app notification that can be switched off in Admin. Group: Order (order updates), Promo (offers &amp; reminders), Team.</summary>
public record PushEventInfo(string Key, string Label, string Audience, string Description, string Group);

/// <summary>
/// Every app notification: one for each switchable email, plus the ones that only go to the app (offers, new arrivals and
/// reminders, sent by <see cref="PushAutomationWorker"/>).
/// </summary>
public static class PushEvents
{
    public const string OfferLive = "OfferLive", NewArrivals = "NewArrivals", CartReminder = "CartReminder",
        WishlistPriceDrop = "WishlistPriceDrop", WishlistBackInStock = "WishlistBackInStock",
        PaymentReminder = "PaymentReminder", ReviewReminder = "ReviewReminder";

    /// <summary>App only (no email).</summary>
    public static readonly PushEventInfo[] AppOnly =
    [
        new(PaymentReminder, "Payment pending", "Customer", "An hour after an online order is placed but not paid — tap to pay.", "Order"),
        new(ReviewReminder, "Rate your purchase", "Customer", "3 days after delivery, if they haven't rated it yet.", "Order"),
        new(OfferLive, "Offer is live", "Customer", "When an offer starts — opens the offer page, with its banner.", "Promo"),
        new(NewArrivals, "New arrivals", "Customer", "Newly published products, at most once a day — opens the product, or the newest products.", "Promo"),
        new(CartReminder, "Cart waiting", "Customer", "3 hours after something was left in the cart (once).", "Promo"),
        new(WishlistPriceDrop, "Wishlist price drop", "Customer", "A product on their wishlist got at least 3% cheaper.", "Promo"),
        new(WishlistBackInStock, "Wishlist back in stock", "Customer", "A product on their wishlist is available again.", "Promo"),
    ];

    public static readonly PushEventInfo[] All =
    [
        .. EmailEvents.All.Select(e => new PushEventInfo(e.Key, e.Label, e.Audience, e.Description, e.Audience == "Admin" ? "Team" : "Order")),
        .. AppOnly
    ];

    public static readonly HashSet<string> Keys = All.Select(e => e.Key).ToHashSet();
}
