using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

/// <summary>
/// A phone or computer that turned on Admin notifications (Admin → Email &amp; notifications). The browser gives the endpoint and
/// keys; a notification goes to the endpoint encrypted for that device only. Removed when the device turns them off or the
/// browser says the subscription is gone.
/// </summary>
public class PushSubscription : BaseEntity
{
    public int UserId { get; set; }
    public AppUser? User { get; set; }
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
}

/// <summary>The server's own notification keys (VAPID), made once on first use. The private key is stored encrypted.</summary>
public class PushConfig : BaseEntity
{
    public string PublicKey { get; set; } = "";
    public string PrivateKeyProtected { get; set; } = "";
}
