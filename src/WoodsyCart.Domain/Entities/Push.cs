using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

/// <summary>
/// A phone or computer that turned on app notifications: a team member in Admin (IsAdmin), or a customer on the storefront
/// (linked to their email and the order they turned it on from). The browser gives the endpoint and keys; a notification goes
/// to the endpoint encrypted for that device only. Removed when the device turns them off or the browser says it's gone.
/// </summary>
public class PushSubscription : BaseEntity
{
    /// <summary>Admin team device (gets team notifications); otherwise a customer's device.</summary>
    public bool IsAdmin { get; set; }
    /// <summary>The signed-in team member or customer; empty for a guest customer.</summary>
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    /// <summary>Customer devices: the email their order updates go to (lower case).</summary>
    public string? Email { get; set; }
    /// <summary>Customer devices: the order the customer turned updates on from (covers orders without an email).</summary>
    public int? OrderId { get; set; }
    /// <summary>The browser's push service address for this device (Google, Apple, Mozilla or Microsoft).</summary>
    public string Endpoint { get; set; } = "";
    /// <summary>The device's public key (base64url).</summary>
    public string P256dh { get; set; } = "";
    /// <summary>The device's secret for this subscription (base64url).</summary>
    public string Auth { get; set; } = "";
    /// <summary>"Android · Chrome", "iPhone · Safari" … from the browser, so Admin can tell the devices apart.</summary>
    public string? Device { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string? LastError { get; set; }
    /// <summary>Customer devices: offers, new arrivals and reminders (off = order updates only). Set by the customer.</summary>
    public bool Promotions { get; set; } = true;
    /// <summary>Last offer / new-arrivals / reminder sent automatically (at most one a day per device).</summary>
    public DateTime? LastPromoAt { get; set; }
    /// <summary>The cart on this device (different products in it, the latest one), as the storefront reports it, for "your cart is waiting".</summary>
    public int CartCount { get; set; }
    public int? CartProductId { get; set; }
    public DateTime? CartUpdatedAt { get; set; }
    public DateTime? CartRemindedAt { get; set; }
}

/// <summary>
/// The server's own notification keys (VAPID), made once on first use (the private key is stored encrypted), and which app
/// notifications go out (Admin → Email &amp; notifications → App notifications).
/// </summary>
public class PushConfig : BaseEntity
{
    public string PublicKey { get; set; } = "";
    public string PrivateKeyProtected { get; set; } = "";
    public bool CustomersEnabled { get; set; } = true;
    public bool TeamEnabled { get; set; } = true;
    /// <summary>{"OrderPlaced": false, …}: notifications switched off; anything not listed is on.</summary>
    public string? EventsJson { get; set; }
    /// <summary>When the last "new arrivals" notification went out (at most one a day).</summary>
    public DateTime? LastNewArrivalsAt { get; set; }
}

/// <summary>A notification sent to many customers at once: written in Admin, or an offer / new arrivals sent automatically.</summary>
public class PushCampaign : BaseEntity
{
    /// <summary>Custom, OfferLive, NewArrivals.</summary>
    public string Kind { get; set; } = "Custom";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string Url { get; set; } = "";
    public string? ImageUrl { get; set; }
    /// <summary>All, Ordered, NotOrdered.</summary>
    public string Audience { get; set; } = "All";
    public int Devices { get; set; }
    public int Sent { get; set; }
    public int Failed { get; set; }
    public string? CreatedBy { get; set; }
}
