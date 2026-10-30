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

    // YouTube Shorts: the product reel uploaded to the YouTube channel (Google login; the client secret and the refresh
    // token are stored encrypted, since Google needs both to renew the hour-long access)
    public string? YouTubeClientId { get; set; }
    public string? YouTubeClientSecretProtected { get; set; }
    public string? YouTubeRefreshTokenProtected { get; set; }
    public string? YouTubeChannelId { get; set; }
    public string? YouTubeChannelTitle { get; set; }
    public string? YouTubeHandle { get; set; }
    public DateTime? YouTubeConnectedAt { get; set; }
    public bool YouTubeEnabled { get; set; } = true;
    public string YouTubeTimes { get; set; } = "17:00";    // India time, one Short per time (at most 2 a day)

    // Reels: a short video made from the product's photos, posted to Instagram and the Facebook Page at their own times
    public bool ReelsEnabled { get; set; }
    public string ReelTimes { get; set; } = "18:00";       // India time, one reel per time
    public bool ReelMusic { get; set; } = true;            // use an uploaded track (none uploaded = no music)

    // Stories: a 9:16 picture of a product on Instagram and Facebook stories (gone after 24 hours). Automatic stories pick a
    // product at each time (offers first, then recent posts, then the longest without a story); off = only by hand
    public bool StoriesEnabled { get; set; }
    public string StoryTimes { get; set; } = "19:00";      // India time, one story per time

    // Product feed for Meta Commerce Manager (catalog): public at /feeds/meta-catalog.csv while switched on
    public bool FeedEnabled { get; set; }
    public DateTime? FeedFetchedAt { get; set; }
    public string? FeedFetchedBy { get; set; }

    // Performance and comments: problems reading them (usually a permission not granted yet), as JSON {"Meta:stats": "…"};
    // cleared when that account is connected again
    public string? EngagementIssues { get; set; }
    // Reading comments / numbers from the accounts at all (off = the worker makes none of those calls; replying by hand still works)
    public bool ReadComments { get; set; }
    public bool ReadStats { get; set; }
    public DateTime? CommentsEmailedAt { get; set; }      // last "new comments" email (at most one every 30 minutes)

    // Reply assistant: answers price / size / colour … questions from the product's details (keyword rules, then Claude for
    // the rest when switched on). Suggest = drafts in Admin; Auto = sent, at most AutoRepliesPerHour
    public AutoReplyMode AutoReplyMode { get; set; } = AutoReplyMode.Suggest;
    public bool AutoReplyAi { get; set; }
    public string? AiKeyProtected { get; set; }            // Anthropic API key, encrypted
    public string AiModel { get; set; } = "claude-opus-5-5";
    public int AutoRepliesPerHour { get; set; } = 20;

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

    // Performance, read from the platform now and then (null = the platform doesn't give that number)
    public int? Views { get; set; }
    public int? Reach { get; set; }
    public int? Likes { get; set; }
    public int? Comments { get; set; }
    public int? Shares { get; set; }
    public int? Saves { get; set; }
    public DateTime? StatsAt { get; set; }
    public DateTime? CommentsCheckedAt { get; set; }
}

/// <summary>A comment on one of our posts (or a reply under it). Replies point at the top comment of their conversation;
/// the top comment carries the read state for the whole conversation.</summary>
public class SocialComment : BaseEntity
{
    public int SocialPostId { get; set; }
    public SocialPost? Post { get; set; }
    public SocialPlatform Platform { get; set; }
    public string ExternalId { get; set; } = "";
    public int? ParentId { get; set; }                     // the top comment; null = this is a top comment
    public SocialComment? Parent { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime PostedAt { get; set; }                 // UTC, as the platform reports it
    public bool FromUs { get; set; }                       // our own reply (from Admin or the app)
    public DateTime? ReadAt { get; set; }                  // top comments only: null = new activity to look at
    public DateTime LastActivityAt { get; set; }           // top comments only: newest comment in the conversation

    // Reply assistant (top comments only): the drafted answer to the newest question in the conversation
    public AutoReplyState ReplyState { get; set; }
    public string? SuggestedReply { get; set; }
    public string? SuggestedTopic { get; set; }            // e.g. "Price, Size"
    public string? SuggestedSource { get; set; }           // "Rules" or "AI"
    public bool SuggestedComplete { get; set; }            // every question answered from real details (may be sent automatically)
    public bool Auto { get; set; }                         // our reply, sent by the assistant
}
