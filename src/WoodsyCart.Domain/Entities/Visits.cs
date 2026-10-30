using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

/// <summary>
/// One storefront visit (ends after 30 minutes without activity), for Admin → Visitors. CreatedAt is when it started.
/// No pages, location or IP address are kept.
/// </summary>
public class Visit : BaseEntity
{
    /// <summary>Random ID the storefront keeps on the phone / browser; holds nothing personal.</summary>
    public Guid VisitorId { get; set; }
    /// <summary>Set once the customer signs in during the visit; empty for guests.</summary>
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    /// <summary>instagram, facebook, threads, google-ads, google, other or direct (decided when the visit starts).</summary>
    public string Source { get; set; } = VisitSources.Direct;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    /// <summary>Time the site was on screen; a tab in the background doesn't count.</summary>
    public int ActiveSeconds { get; set; }
}

public static class VisitSources
{
    public const string Instagram = "instagram", Facebook = "facebook", Threads = "threads",
        GoogleAds = "google-ads", Google = "google", Other = "other", Direct = "direct";
    public static readonly string[] All = [Instagram, Facebook, Threads, GoogleAds, Google, Other, Direct];
}

/// <summary>One run of the API (from start until it stopped), so Admin can see restarts and downtime. CreatedAt is the start.</summary>
public class ServerRun : BaseEntity
{
    /// <summary>Updated every minute while the API is running.</summary>
    public DateTime LastAliveAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A time visitor statistics were switched off in Admin (CreatedAt = switched off; EndedAt empty while still off).</summary>
public class VisitStatsPause : BaseEntity
{
    public DateTime? EndedAt { get; set; }
}
