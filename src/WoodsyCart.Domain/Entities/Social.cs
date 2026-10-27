using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

/// <summary>Instagram / Facebook / Threads posting settings (Admin → Marketing → Social media). One row.</summary>
public class SocialConfig : BaseEntity
{
    // Connected accounts (through a Meta app with Facebook Login; the Page token is stored encrypted)
    public string? MetaAppId { get; set; }
    public string? PageId { get; set; }
    public string? PageName { get; set; }
    public string? PageTokenProtected { get; set; }
    public string? InstagramUserId { get; set; }
    public string? InstagramUsername { get; set; }
    public DateTime? ConnectedAt { get; set; }

    public bool InstagramEnabled { get; set; } = true;
    public bool FacebookEnabled { get; set; } = true;

    // Threads (its own login: the Threads app ID + secret from the same Meta app; the long-lived token is stored encrypted
    // and refreshed weekly, since it lasts 60 days)
    public string? ThreadsAppId { get; set; }
    public string? ThreadsUserId { get; set; }
    public string? ThreadsUsername { get; set; }
    public string? ThreadsTokenProtected { get; set; }
    public DateTime? ThreadsTokenExpiresAt { get; set; }
    public DateTime? ThreadsTokenRefreshedAt { get; set; }
    public DateTime? ThreadsConnectedAt { get; set; }
    public bool ThreadsEnabled { get; set; } = true;
    public string? ThreadsTemplate { get; set; }          // null = built-in short template (Threads allows 500 characters)
    public string? ThreadsTopic { get; set; } = "Furniture";   // Threads allows one topic tag per post

    // Reels: a short video made from the product's photos, posted to Instagram and the Facebook Page at their own times
    public bool ReelsEnabled { get; set; }
    public string ReelTimes { get; set; } = "18:00";       // India time, one reel per time
    public bool ReelMusic { get; set; } = true;            // use an uploaded track (none uploaded = no music)

    // Product feed for Meta Commerce Manager (catalog): public at /feeds/meta-catalog.csv while switched on
    public bool FeedEnabled { get; set; }
    public DateTime? FeedFetchedAt { get; set; }
    public string? FeedFetchedBy { get; set; }

    // Caption
    public string? Template { get; set; }                 // null = built-in template
    public bool IncludePrice { get; set; } = true;
    public bool ShowMrp { get; set; } = true;
    public string? WhatsApp { get; set; }
    public string? City { get; set; }                      // null = business city
    public string? Hashtags { get; set; }                  // extra hashtags, space or comma separated
    public int MaxHashtags { get; set; } = 5;

    // Pace
    public string PostTimes { get; set; } = "11:00,15:00,20:30";   // India time, one product per time
    public bool AutoQueueNewProducts { get; set; }
    public DateTime? AutoQueueSince { get; set; }          // products published after this are queued automatically
}

/// <summary>One product's post on one platform. Each product has at most one post per platform.</summary>
public class SocialPost : BaseEntity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public SocialPlatform Platform { get; set; }
    public SocialPostStatus Status { get; set; }
    public DateTime? ScheduledAt { get; set; }             // UTC
    public DateTime? PublishedAt { get; set; }
    public string? ExternalId { get; set; }                // Instagram media id / Facebook post id / Threads media id
    public string? Permalink { get; set; }
    public string? CaptionOverride { get; set; }           // edited in Admin before posting
    public string? PostedCaption { get; set; }             // exactly what is live
    public string? PostedSnapshot { get; set; }            // JSON of the details the caption was built from (to show what changed)
    public string? ContentHash { get; set; }               // caption + photos as posted
    public int PhotoCount { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
}
