using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

/// <summary>Instagram / Facebook posting settings (Admin → Marketing → Social media). One row.</summary>
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
    public string? ExternalId { get; set; }                // Instagram media id / Facebook post id
    public string? Permalink { get; set; }
    public string? CaptionOverride { get; set; }           // edited in Admin before posting
    public string? PostedCaption { get; set; }             // exactly what is live
    public string? PostedSnapshot { get; set; }            // JSON of the details the caption was built from (to show what changed)
    public string? ContentHash { get; set; }               // caption + photos as posted
    public int PhotoCount { get; set; }
    public string? LastError { get; set; }
    public int Attempts { get; set; }
}
