using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Social;

public record SocialSettingsDto(
    bool Connected, string? MetaAppId, string? PageId, string? PageName, string? InstagramUserId, string? InstagramUsername, DateTime? ConnectedAt,
    bool InstagramEnabled, bool FacebookEnabled, string Template, bool CustomTemplate, string DefaultTemplate, string[] Tokens,
    bool IncludePrice, bool ShowMrp, string? WhatsApp, string? City, string? Hashtags, int MaxHashtags, string PostTimes,
    bool AutoQueueNewProducts, bool PublicPhotos);
public record SocialSettingsUpdate(bool InstagramEnabled, bool FacebookEnabled, string? Template, bool IncludePrice, bool ShowMrp,
    string? WhatsApp, string? City, string? Hashtags, int MaxHashtags, string PostTimes, bool AutoQueueNewProducts);
public record SocialConnectRequest(string AppId, string AppSecret, string UserToken, string? PageId);
public record SocialPageChoice(string Id, string Name, string? Instagram);
public record SocialConnectResult(bool Connected, List<SocialPageChoice>? Pages, string? Message);
public record SocialQueueRequest(List<int> ProductIds, bool Now = false, List<SocialPlatform>? Platforms = null);
public record SocialQueueResult(int Products, int PostingNow, int Scheduled, int Skipped, DateTime? FirstSlot, string? Note);
public record SocialPlatformState(SocialPlatform Platform, bool Enabled, int? PostId, SocialPostStatus? Status, DateTime? ScheduledAt,
    DateTime? PublishedAt, string? Permalink, string? LastError, List<string> Changes, string? PostedCaption, bool CustomCaption);
public record SocialProductDto(int ProductId, string Caption, string Code, List<string> Photos, bool Connected, bool Publishable, List<SocialPlatformState> Platforms);
public record SocialPostRow(int Id, int ProductId, string ProductName, string? Image, SocialPlatform Platform, SocialPostStatus Status,
    DateTime? ScheduledAt, DateTime? PublishedAt, string? Permalink, string? LastError, List<string> Changes, bool CustomCaption);
public record SocialQuota(SocialPlatform Platform, int Used, int Limit, bool FromMeta);

/// <summary>
/// Posts products to Instagram and the Facebook Page: one post per product per platform, at a steady pace (Admin → Social
/// media). Facebook posts follow product changes automatically; Instagram can't edit captions through the API, so a
/// changed product is marked "out of date" for the admin to copy the new caption or replace the post.
/// </summary>
public class SocialService(AppDbContext db, MetaClient meta, IDataProtectionProvider dp, FileStorage storage, IMemoryCache cache,
    IOptions<SmtpOptions> urls, EmailSender email, ILogger<SocialService> logger)
{
    public const int PhotoWidth = 1080, PhotoHeight = 1350;     // 4:5 portrait: the most screen space in the feed
    private readonly IDataProtector _protector = dp.CreateProtector("WoodsyCart.SocialSecrets.v1");
    private static readonly SocialPostStatus[] Live = [SocialPostStatus.Published, SocialPostStatus.OutOfDate];
    private static readonly SocialPostStatus[] Taken = [SocialPostStatus.Queued, SocialPostStatus.Publishing, SocialPostStatus.Published, SocialPostStatus.OutOfDate];

    // ---------- Settings & connection ----------

    public async Task<SocialConfig> ConfigAsync()
    {
        var c = await db.SocialConfigs.FirstOrDefaultAsync();
        if (c is not null) return c;
        var biz = await db.BusinessSettings.AsNoTracking().FirstAsync();
        c = new SocialConfig { WhatsApp = biz.WhatsApp, Hashtags = "#FurnitureBangalore #HomeDecor" };
        db.SocialConfigs.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    public async Task<SocialSettingsDto> SettingsAsync()
    {
        var c = await ConfigAsync();
        return new SocialSettingsDto(IsConnected(c), c.MetaAppId, c.PageId, c.PageName, c.InstagramUserId, c.InstagramUsername, c.ConnectedAt,
            c.InstagramEnabled, c.FacebookEnabled, c.Template ?? SocialCaption.DefaultTemplate, c.Template is not null, SocialCaption.DefaultTemplate,
            SocialCaption.Tokens, c.IncludePrice, c.ShowMrp, c.WhatsApp, c.City, c.Hashtags, c.MaxHashtags, c.PostTimes, c.AutoQueueNewProducts,
            PublicBase() is not null);
    }

    public async Task SaveSettingsAsync(SocialSettingsUpdate r)
    {
        var times = ParseTimes(r.PostTimes);
        if (times.Count == 0) throw new AppException("Enter at least one posting time, e.g. 11:00, 15:00, 20:30.");
        if (r.MaxHashtags is < 1 or > 30) throw new AppException("Hashtags per post must be between 1 and 30 (Instagram allows 30; 3–5 work best).");
        var template = string.IsNullOrWhiteSpace(r.Template) || r.Template.Replace("\r", "").Trim() == SocialCaption.DefaultTemplate.Trim() ? null : r.Template.Trim();
        if (template is not null && !template.Contains("{name}")) throw new AppException("The caption template must contain {name}.");
        var c = await ConfigAsync();
        if (r.AutoQueueNewProducts && !c.AutoQueueNewProducts) c.AutoQueueSince = DateTime.UtcNow;
        c.InstagramEnabled = r.InstagramEnabled; c.FacebookEnabled = r.FacebookEnabled; c.Template = template;
        c.IncludePrice = r.IncludePrice; c.ShowMrp = r.ShowMrp; c.WhatsApp = Clean(r.WhatsApp); c.City = Clean(r.City); c.Hashtags = Clean(r.Hashtags);
        c.MaxHashtags = r.MaxHashtags; c.PostTimes = string.Join(",", times.Select(t => t.ToString(@"hh\:mm")));
        c.AutoQueueNewProducts = r.AutoQueueNewProducts;
        await db.SaveChangesAsync();
    }

    /// <summary>Connects the Facebook Page (and the Instagram account linked to it). The app secret and user token are
    /// used once and never stored; only the Page token is kept, encrypted.</summary>
    public async Task<SocialConnectResult> ConnectAsync(SocialConnectRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.AppId) || string.IsNullOrWhiteSpace(r.AppSecret) || string.IsNullOrWhiteSpace(r.UserToken))
            throw new AppException("Enter the App ID, App secret and the access token from Graph API Explorer.");
        List<MetaPage> pages;
        try { pages = await meta.PagesAsync(r.AppId.Trim(), r.AppSecret.Trim(), r.UserToken.Trim()); }
        catch (MetaException ex) { throw new AppException("Meta refused the connection: " + ex.Message); }
        if (pages.Count == 0) return new SocialConnectResult(false, null, "This token can't see any Facebook Page. Tick your Page when Meta asks which Pages to allow.");
        var page = r.PageId is null ? (pages.Count == 1 ? pages[0] : null) : pages.FirstOrDefault(p => p.Id == r.PageId);
        if (page is null)
            return new SocialConnectResult(false, pages.Select(p => new SocialPageChoice(p.Id, p.Name, p.InstagramUsername)).ToList(), "Choose the Page to post to.");

        var c = await ConfigAsync();
        c.MetaAppId = r.AppId.Trim(); c.PageId = page.Id; c.PageName = page.Name; c.PageTokenProtected = _protector.Protect(page.Token);
        c.InstagramUserId = page.InstagramId; c.InstagramUsername = page.InstagramUsername; c.ConnectedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        cache.Remove(QuotaKey);
        return new SocialConnectResult(true, null, page.InstagramId is null
            ? $"Connected to {page.Name}, but no Instagram account is linked to it. Link Instagram to the Page, then connect again."
            : $"Connected: Facebook Page {page.Name} and Instagram @{page.InstagramUsername}.");
    }

    public async Task DisconnectAsync()
    {
        var c = await ConfigAsync();
        c.PageTokenProtected = null; c.ConnectedAt = null;
        await db.SaveChangesAsync();
    }

    private static bool IsConnected(SocialConfig c) => c.PageTokenProtected is not null && c.PageId is not null;
    private string Token(SocialConfig c) => _protector.Unprotect(c.PageTokenProtected!);

    private static List<SocialPlatform> Enabled(SocialConfig c)
    {
        var list = new List<SocialPlatform>();
        if (c.InstagramEnabled && c.InstagramUserId is not null) list.Add(SocialPlatform.Instagram);
        if (c.FacebookEnabled && c.PageId is not null) list.Add(SocialPlatform.Facebook);
        return list;
    }

    // ---------- Content ----------

    private IQueryable<Product> WithDetails() => db.Products.AsNoTracking()
        .Include(p => p.Category).Include(p => p.Offer).Include(p => p.Tags)
        .Include(p => p.Variants).Include(p => p.Options).ThenInclude(o => o.Values)
        .Include(p => p.Images);

    /// <summary>Product photos in their shop order (general photos before colour-specific ones), at most 10.</summary>
    private static List<string> Photos(Product p) => p.Images
        .Where(i => i.MediaType == MediaType.Image && i.LocalPath != null)
        .OrderBy(i => i.VariantId != null).ThenBy(i => i.SortOrder).ThenBy(i => i.Id).Select(i => i.LocalPath!).Take(SocialCaption.MaxPhotos).ToList();

    private async Task<PostContent> ContentAsync(Product p, SocialConfig c, string? overrideCaption, BusinessSettings? biz = null) =>
        SocialCaption.Build(p, c, biz ?? await db.BusinessSettings.AsNoTracking().FirstAsync(), overrideCaption, Photos(p));

    public async Task<SocialProductDto> ProductAsync(int productId)
    {
        var p = await WithDetails().FirstOrDefaultAsync(x => x.Id == productId) ?? throw new AppException("Product not found", 404);
        // Check this product's posts now instead of waiting for the 10-minute run, so the card shows the current status
        await RefreshAsync([productId]);
        var c = await ConfigAsync();
        var posts = await db.SocialPosts.AsNoTracking().Where(s => s.ProductId == productId).ToListAsync();
        var biz = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var plain = await ContentAsync(p, c, null, biz);
        var enabled = Enabled(c);
        var platforms = new[] { SocialPlatform.Instagram, SocialPlatform.Facebook }.Select(pl =>
        {
            var s = posts.FirstOrDefault(x => x.Platform == pl);
            var now = s?.CaptionOverride is null ? plain : SocialCaption.Build(p, c, biz, s.CaptionOverride, plain.Photos);
            var changes = s is { Status: SocialPostStatus.OutOfDate } ? SocialCaption.Changes(s.PostedSnapshot, now.Snapshot) : [];
            return new SocialPlatformState(pl, enabled.Contains(pl), s?.Id, s?.Status, s?.ScheduledAt, s?.PublishedAt, s?.Permalink, s?.LastError,
                changes, s?.PostedCaption, s?.CaptionOverride is not null);
        }).ToList();
        return new SocialProductDto(p.Id, plain.Caption, SocialCaption.Code(p.Id), plain.Photos, IsConnected(c),
            p.IsActive && plain.Photos.Count > 0, platforms);
    }

    /// <summary>The caption the post should have now (custom caption if one was saved).</summary>
    public async Task<string> CurrentCaptionAsync(int postId)
    {
        var s = await db.SocialPosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == postId) ?? throw new AppException("Post not found", 404);
        var p = await WithDetails().FirstAsync(x => x.Id == s.ProductId);
        return (await ContentAsync(p, await ConfigAsync(), s.CaptionOverride)).Caption;
    }

    // ---------- Queue ----------

    /// <summary>Adds products to the queue. "Now" posts as many as today's limit allows straight away; the rest (or all,
    /// when not "now") get the next free posting times. A product already posted or queued on a platform is skipped there.</summary>
    public async Task<SocialQueueResult> QueueAsync(SocialQueueRequest r)
    {
        var c = await ConfigAsync();
        if (!IsConnected(c)) throw new AppException("Connect Instagram and Facebook first (Social media → Settings).");
        var platforms = Enabled(c).Where(p => r.Platforms is null || r.Platforms.Contains(p)).ToList();
        if (platforms.Count == 0) throw new AppException("Switch on Instagram or Facebook in Social media settings.");

        var ids = r.ProductIds.Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.IsActive, HasPhoto = p.Images.Any(i => i.MediaType == MediaType.Image && i.LocalPath != null) }).ToListAsync();
        var existing = await db.SocialPosts.Where(s => ids.Contains(s.ProductId)).ToListAsync();

        var work = new List<(int ProductId, List<SocialPlatform> Platforms)>();
        var skipped = 0;
        foreach (var id in ids)
        {
            var p = products.FirstOrDefault(x => x.Id == id);
            if (p is null || !p.IsActive || !p.HasPhoto) { skipped++; continue; }
            var need = platforms.Where(pl => !existing.Any(s => s.ProductId == id && s.Platform == pl && Taken.Contains(s.Status))).ToList();
            if (need.Count == 0) { skipped++; continue; }
            work.Add((id, need));
        }
        if (work.Count == 0)
            return new SocialQueueResult(0, 0, 0, skipped, null, "Nothing to add: the products are already posted or queued, unpublished, or have no photos.");

        var nowCount = 0;
        string? note = null;
        if (r.Now)
        {
            var room = await RoomNowAsync(c, platforms);
            nowCount = Math.Min(room, work.Count);
            if (nowCount < work.Count)
                note = room == 0
                    ? "Instagram's daily limit is used up, so all of them were scheduled instead."
                    : $"Only {room} more can be posted in the next 24 hours (Instagram's limit); the rest were scheduled.";
        }
        var slots = await FreeSlotsAsync(c, work.Count - nowCount);
        var at = DateTime.UtcNow;
        for (var i = 0; i < work.Count; i++)
        {
            var when = i < nowCount ? at : slots[i - nowCount];
            foreach (var pl in work[i].Platforms)
            {
                var s = existing.FirstOrDefault(x => x.ProductId == work[i].ProductId && x.Platform == pl);
                if (s is null) { s = new SocialPost { ProductId = work[i].ProductId, Platform = pl }; db.SocialPosts.Add(s); }
                s.Status = SocialPostStatus.Queued; s.ScheduledAt = when; s.LastError = null;
            }
        }
        await db.SaveChangesAsync();
        return new SocialQueueResult(work.Count, nowCount, work.Count - nowCount, skipped, slots.Count > 0 ? slots[0] : null, note);
    }

    /// <summary>How many more products can be posted in the next 24 hours (Instagram is the limiting platform).</summary>
    private async Task<int> RoomNowAsync(SocialConfig c, List<SocialPlatform> platforms)
    {
        if (!platforms.Contains(SocialPlatform.Instagram)) return int.MaxValue;
        var q = await QuotaAsync(c);
        var dueAlready = await db.SocialPosts.CountAsync(s => s.Platform == SocialPlatform.Instagram
            && (s.Status == SocialPostStatus.Publishing || (s.Status == SocialPostStatus.Queued && s.ScheduledAt <= DateTime.UtcNow)));
        return Math.Max(0, q.Limit - q.Used - dueAlready);
    }

    private const string QuotaKey = "social:ig-quota";

    /// <summary>Instagram's own counter when reachable (cached briefly), otherwise our own 24-hour count against 100.</summary>
    public async Task<SocialQuota> QuotaAsync(SocialConfig? cfg = null, bool fresh = false)
    {
        var c = cfg ?? await ConfigAsync();
        var since = DateTime.UtcNow.AddHours(-24);
        var ours = await db.SocialPosts.CountAsync(s => s.Platform == SocialPlatform.Instagram && s.PublishedAt >= since);
        if (!IsConnected(c) || c.InstagramUserId is null) return new SocialQuota(SocialPlatform.Instagram, ours, 100, false);
        if (fresh) cache.Remove(QuotaKey);
        try
        {
            return await cache.GetOrCreateAsync(QuotaKey, async e =>
            {
                e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
                var (used, limit) = await meta.InstagramQuotaAsync(c.InstagramUserId!, Token(c));
                return new SocialQuota(SocialPlatform.Instagram, Math.Max(used, ours), limit, true);
            }) ?? new SocialQuota(SocialPlatform.Instagram, ours, 100, false);
        }
        catch (Exception ex) when (ex is MetaException or HttpRequestException)
        {
            logger.LogWarning("Could not read Instagram's publishing limit: {Message}", ex.Message);
            return new SocialQuota(SocialPlatform.Instagram, ours, 100, false);
        }
    }

    /// <summary>The next posting times (from Settings, India time) that no queued product holds yet.</summary>
    private async Task<List<DateTime>> FreeSlotsAsync(SocialConfig c, int count)
    {
        var result = new List<DateTime>();
        if (count <= 0) return result;
        var now = DateTime.UtcNow;
        var taken = (await db.SocialPosts.Where(s => s.Status == SocialPostStatus.Queued && s.ScheduledAt > now).Select(s => s.ScheduledAt!.Value).ToListAsync())
            .Select(d => new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, DateTimeKind.Utc)).ToHashSet();
        var times = ParseTimes(c.PostTimes);
        if (times.Count == 0) times = [new TimeSpan(11, 0, 0)];
        var today = TimeZoneInfo.ConvertTimeFromUtc(now, India).Date;
        for (var day = 0; result.Count < count && day < 3660; day++)
            foreach (var t in times)
            {
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(today.AddDays(day) + t, DateTimeKind.Unspecified), India);
                if (utc <= now.AddMinutes(1) || !taken.Add(utc)) continue;
                result.Add(utc);
                if (result.Count == count) break;
            }
        return result;
    }

    public async Task RemoveFromQueueAsync(int postId)
    {
        var s = await Post(postId);
        if (s.Status is not (SocialPostStatus.Queued or SocialPostStatus.Failed)) throw new AppException("Only queued or failed posts can be removed from the queue.");
        s.Status = SocialPostStatus.Removed; s.ScheduledAt = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Deletes the live post on Instagram or Facebook. The product counts as not posted there again.</summary>
    public async Task DeletePostAsync(int postId)
    {
        var s = await Post(postId);
        if (!Live.Contains(s.Status)) throw new AppException("This post isn't live.");
        if (s.ExternalId is not null)
        {
            var c = await ConfigAsync();
            if (!IsConnected(c)) throw new AppException("Connect Instagram and Facebook in Settings first.");
            try { await meta.DeleteAsync(s.ExternalId, Token(c)); }
            catch (MetaException ex) when (ex.Code is 100) { }   // already deleted in the app
            catch (MetaException ex) when (ex.Code is 10 or 200 && s.Platform == SocialPlatform.Instagram)
            {
                throw new AppException("Instagram needs the instagram_manage_contents permission to delete posts. Add it to the Meta app, then reconnect in Settings with a new token.");
            }
            catch (MetaException ex) { throw new AppException($"Couldn't delete the {s.Platform} post: " + ex.Message); }
        }
        s.Status = SocialPostStatus.Removed; s.ExternalId = null; s.Permalink = null; s.PublishedAt = null; s.ScheduledAt = null; s.LastError = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Try a failed post again now (or at the next free time when the daily limit is used up).</summary>
    public async Task RetryAsync(int postId)
    {
        var s = await Post(postId);
        if (s.Status != SocialPostStatus.Failed) throw new AppException("Only failed posts can be retried.");
        var c = await ConfigAsync();
        s.Status = SocialPostStatus.Queued; s.LastError = null;
        s.ScheduledAt = s.Platform == SocialPlatform.Facebook || await RoomNowAsync(c, [SocialPlatform.Instagram]) > 0
            ? DateTime.UtcNow : (await FreeSlotsAsync(c, 1))[0];
        await db.SaveChangesAsync();
    }

    /// <summary>Saves a custom caption for this post (empty = back to the template). A live Facebook post is updated straight away.</summary>
    public async Task SetCaptionAsync(int postId, string? caption)
    {
        var s = await Post(postId);
        s.CaptionOverride = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (s.CaptionOverride?.Length > SocialCaption.MaxLength) throw new AppException($"Captions can be at most {SocialCaption.MaxLength} characters.");
        await db.SaveChangesAsync();
        if (s.Platform == SocialPlatform.Facebook && Live.Contains(s.Status)) await RefreshAsync([s.ProductId]);
    }

    /// <summary>After pasting the new caption into the Instagram app: the post counts as up to date again.</summary>
    public async Task MarkUpdatedAsync(int postId)
    {
        var s = await Post(postId);
        if (!Live.Contains(s.Status)) throw new AppException("This post isn't live.");
        var p = await WithDetails().FirstAsync(x => x.Id == s.ProductId);
        Apply(s, await ContentAsync(p, await ConfigAsync(), s.CaptionOverride));
        s.Status = SocialPostStatus.Published;
        await db.SaveChangesAsync();
    }

    /// <summary>Deletes the live post and posts the product again (likes and comments on the old post are lost).</summary>
    public async Task ReplaceAsync(int postId)
    {
        var s = await Post(postId);
        if (!Live.Contains(s.Status)) throw new AppException("Only live posts can be replaced.");
        var c = await ConfigAsync();
        if (s.Platform == SocialPlatform.Instagram && await RoomNowAsync(c, [SocialPlatform.Instagram]) == 0)
            throw new AppException("Instagram's daily limit is used up. Replace the post tomorrow.");
        if (s.ExternalId is not null)
        {
            try { await meta.DeleteAsync(s.ExternalId, Token(c)); }
            catch (MetaException ex) when (ex.Code is not 190) { throw new AppException("Couldn't delete the old post: " + ex.Message); }
        }
        s.Status = SocialPostStatus.Queued; s.ScheduledAt = DateTime.UtcNow; s.ExternalId = null; s.Permalink = null; s.PublishedAt = null;
        await db.SaveChangesAsync();
    }

    // ---------- Lists ----------

    public async Task<PagedResult<SocialPostRow>> ListAsync(SocialPostStatus? status, SocialPlatform? platform, int page, int pageSize)
    {
        var q = db.SocialPosts.AsNoTracking().Where(s => s.Status != SocialPostStatus.Removed);
        if (status is not null) q = q.Where(s => s.Status == status);
        if (platform is not null) q = q.Where(s => s.Platform == platform);
        q = status == SocialPostStatus.Queued ? q.OrderBy(s => s.ScheduledAt).ThenBy(s => s.ProductId) : q.OrderByDescending(s => s.PublishedAt ?? s.UpdatedAt ?? s.CreatedAt);
        var total = await q.CountAsync();
        pageSize = Math.Clamp(pageSize, 1, 100);
        var rows = await q.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).Select(s => new
        {
            Post = s, s.Product!.Name,
            Image = s.Product.Images.OrderBy(i => i.SortOrder).Select(i => i.LocalThumbnailPath ?? i.LocalPath).FirstOrDefault()
        }).ToListAsync();

        var changes = new Dictionary<int, List<string>>();
        var outdated = rows.Where(r => r.Post.Status == SocialPostStatus.OutOfDate).ToList();
        if (outdated.Count > 0)
        {
            var c = await ConfigAsync();
            var biz = await db.BusinessSettings.AsNoTracking().FirstAsync();
            var pids = outdated.Select(r => r.Post.ProductId).Distinct().ToList();
            foreach (var p in await WithDetails().Where(p => pids.Contains(p.Id)).ToListAsync())
                foreach (var r in outdated.Where(o => o.Post.ProductId == p.Id))
                    changes[r.Post.Id] = SocialCaption.Changes(r.Post.PostedSnapshot, (await ContentAsync(p, c, r.Post.CaptionOverride, biz)).Snapshot);
        }
        return new PagedResult<SocialPostRow>
        {
            Items = rows.Select(r => new SocialPostRow(r.Post.Id, r.Post.ProductId, r.Name, r.Image, r.Post.Platform, r.Post.Status, r.Post.ScheduledAt,
                r.Post.PublishedAt, r.Post.Permalink, r.Post.LastError, changes.GetValueOrDefault(r.Post.Id) ?? [], r.Post.CaptionOverride is not null)).ToList(),
            Total = total, Page = page, PageSize = pageSize
        };
    }

    public async Task<object> SummaryAsync()
    {
        var c = await ConfigAsync();
        var counts = await db.SocialPosts.Where(s => s.Status != SocialPostStatus.Removed)
            .GroupBy(s => new { s.Platform, s.Status }).Select(g => new { g.Key.Platform, g.Key.Status, Count = g.Count() }).ToListAsync();
        var since = DateTime.UtcNow.AddHours(-24);
        var fb24 = await db.SocialPosts.CountAsync(s => s.Platform == SocialPlatform.Facebook && s.PublishedAt >= since);
        var next = await db.SocialPosts.Where(s => s.Status == SocialPostStatus.Queued).MinAsync(s => s.ScheduledAt);
        var postedProducts = await db.SocialPosts.Where(s => Live.Contains(s.Status)).Select(s => s.ProductId).Distinct().CountAsync();
        var publishable = await db.Products.CountAsync(p => p.IsActive && p.Images.Any(i => i.MediaType == MediaType.Image && i.LocalPath != null));
        return new
        {
            connected = IsConnected(c), instagram = c.InstagramUsername, page = c.PageName,
            enabled = Enabled(c), quota = await QuotaAsync(c), facebookLast24h = fb24, nextPostAt = next,
            postedProducts, notPosted = Math.Max(0, publishable - postedProducts),
            counts = counts.GroupBy(x => x.Platform.ToString()).ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Status.ToString(), x => x.Count))
        };
    }

    /// <summary>Per product: the status of its Instagram and Facebook post (for the products list).</summary>
    public async Task<Dictionary<int, Dictionary<string, string>>> StatusesAsync(IReadOnlyCollection<int> productIds) =>
        (await db.SocialPosts.AsNoTracking().Where(s => productIds.Contains(s.ProductId) && s.Status != SocialPostStatus.Removed)
            .Select(s => new { s.ProductId, s.Platform, s.Status }).ToListAsync())
        .GroupBy(s => s.ProductId).ToDictionary(g => g.Key, g => g.ToDictionary(s => s.Platform.ToString(), s => s.Status.ToString()));

    // ---------- Background work (SocialWorker) ----------

    /// <summary>Publishes posts whose time has come. Returns how many were attempted.</summary>
    public async Task<int> PublishDueAsync(CancellationToken ct)
    {
        var c = await ConfigAsync();
        if (!IsConnected(c)) return 0;
        // A post interrupted mid-way (API restart) may or may not be on Instagram: don't post it twice automatically.
        var stuck = await db.SocialPosts.Where(s => s.Status == SocialPostStatus.Publishing && s.UpdatedAt < DateTime.UtcNow.AddMinutes(-15)).ToListAsync(ct);
        foreach (var s in stuck) { s.Status = SocialPostStatus.Failed; s.LastError = "Interrupted while posting. Check the account before retrying, so it isn't posted twice."; }
        if (stuck.Count > 0) await db.SaveChangesAsync(ct);

        var due = await db.SocialPosts.Where(s => s.Status == SocialPostStatus.Queued && s.ScheduledAt <= DateTime.UtcNow)
            .OrderBy(s => s.ScheduledAt).ThenBy(s => s.ProductId).ThenBy(s => s.Platform).Take(10).ToListAsync(ct);
        foreach (var s in due) await PublishAsync(s, c, ct);
        return due.Count;
    }

    private async Task PublishAsync(SocialPost s, SocialConfig c, CancellationToken ct)
    {
        var p = await WithDetails().FirstOrDefaultAsync(x => x.Id == s.ProductId, ct);
        if (p is null || !p.IsActive)
        {
            s.Status = SocialPostStatus.Removed; s.LastError = "The product was unpublished before its turn.";
            await db.SaveChangesAsync(ct);
            return;
        }
        if (!Enabled(c).Contains(s.Platform))
        {
            s.Status = SocialPostStatus.Removed; s.LastError = $"{s.Platform} is switched off in Settings.";
            await db.SaveChangesAsync(ct);
            return;
        }
        if (s.Platform == SocialPlatform.Instagram)
        {
            var q = await QuotaAsync(c, fresh: true);
            if (q.Used >= q.Limit)
            {
                s.ScheduledAt = (await FreeSlotsAsync(c, 1))[0];
                s.LastError = $"Instagram's limit of {q.Limit} posts in 24 hours was reached; moved to the next posting time.";
                await db.SaveChangesAsync(ct);
                return;
            }
        }

        var content = await ContentAsync(p, c, s.CaptionOverride);
        if (content.Photos.Count == 0) { await FailAsync(s, "The product has no photos.", ct); return; }
        if (PublicBase() is not { } baseUrl) { await FailAsync(s, "Photos need a public address: set Smtp:ApiUrl (e.g. https://api.woodsycart.com).", ct); return; }

        s.Status = SocialPostStatus.Publishing; s.Attempts++;
        await db.SaveChangesAsync(ct);
        try
        {
            var urls = PrepareJpegs(p.Id, content.Photos).Select(rel => baseUrl + rel).ToList();
            var token = Token(c);
            var result = s.Platform == SocialPlatform.Instagram
                ? await meta.PublishInstagramAsync(c.InstagramUserId!, token, urls, content.Caption, ct)
                : await meta.PublishFacebookAsync(c.PageId!, token, urls, content.Caption, ct);
            s.ExternalId = result.Id; s.Permalink = result.Permalink; s.PublishedAt = DateTime.UtcNow;
            s.Status = SocialPostStatus.Published; s.LastError = null;
            Apply(s, content);
            await db.SaveChangesAsync(ct);
            cache.Remove(QuotaKey);
            logger.LogInformation("Posted product {Product} to {Platform}: {Link}", p.Id, s.Platform, result.Permalink);
        }
        catch (Exception ex) when (ex is MetaException or HttpRequestException or IOException)
        {
            await FailAsync(s, ex.Message, ct);
            if (ex is MetaException { TokenInvalid: true })
                await NotifyAsync("Instagram and Facebook are disconnected",
                    "<p>Meta no longer accepts WoodsyCart's access (the Page token was revoked or the password changed). Posting is paused.</p><p>Connect again in Admin → Social media → Settings.</p>");
        }
    }

    /// <summary>Compares live posts with their products: Facebook posts are updated, Instagram posts marked out of date.</summary>
    public async Task<int> RefreshAsync(IReadOnlyCollection<int>? productIds = null, CancellationToken ct = default)
    {
        var c = await ConfigAsync();
        if (!IsConnected(c)) return 0;
        var q = db.SocialPosts.Where(s => Live.Contains(s.Status));
        if (productIds is not null) q = q.Where(s => productIds.Contains(s.ProductId));
        var posts = await q.ToListAsync(ct);
        if (posts.Count == 0) return 0;
        var biz = await db.BusinessSettings.AsNoTracking().FirstAsync(ct);
        var pids = posts.Select(s => s.ProductId).Distinct().ToList();
        var products = await WithDetails().Where(p => pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var changed = 0;
        foreach (var s in posts)
        {
            if (!products.TryGetValue(s.ProductId, out var p)) continue;   // product deleted: the post stays as it is
            var content = await ContentAsync(p, c, s.CaptionOverride, biz);
            if (content.Hash == s.ContentHash)
            {
                if (s.Status == SocialPostStatus.OutOfDate) { s.Status = SocialPostStatus.Published; changed++; }
                continue;
            }
            var before = string.IsNullOrEmpty(s.PostedSnapshot) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(s.PostedSnapshot);
            var samePhotos = before?.GetValueOrDefault("Photos") == content.Snapshot["Photos"];
            if (s.Platform == SocialPlatform.Facebook && samePhotos && s.ExternalId is not null)
            {
                try
                {
                    await meta.UpdateFacebookAsync(s.ExternalId, Token(c), content.Caption, ct);
                    Apply(s, content);
                    s.Status = SocialPostStatus.Published; s.LastError = null;
                    changed++;
                    continue;
                }
                catch (MetaException ex) { s.LastError = "Couldn't update the Facebook post: " + ex.Message; }
            }
            if (s.Status != SocialPostStatus.OutOfDate) { s.Status = SocialPostStatus.OutOfDate; changed++; }
        }
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>"Post new products automatically": queues products added after the switch was turned on, once published.</summary>
    public async Task<int> AutoQueueAsync(CancellationToken ct)
    {
        var c = await ConfigAsync();
        if (!IsConnected(c) || !c.AutoQueueNewProducts || c.AutoQueueSince is null) return 0;
        var ids = await db.Products.Where(p => p.IsActive && p.CreatedAt >= c.AutoQueueSince
                && p.Images.Any(i => i.MediaType == MediaType.Image && i.LocalPath != null)
                && !db.SocialPosts.Any(s => s.ProductId == p.Id))
            .OrderBy(p => p.Id).Select(p => p.Id).Take(200).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        return (await QueueAsync(new SocialQueueRequest(ids))).Products;
    }

    // ---------- Helpers ----------

    private static void Apply(SocialPost s, PostContent content)
    {
        s.PostedCaption = content.Caption;
        s.PostedSnapshot = JsonSerializer.Serialize(content.Snapshot);
        if (s.PostedSnapshot.Length > 4000) s.PostedSnapshot = JsonSerializer.Serialize(content.Snapshot.Where(kv => kv.Key != "Caption" && kv.Key != "Description").ToDictionary());
        s.ContentHash = content.Hash;
        s.PhotoCount = content.Photos.Count;
    }

    private async Task FailAsync(SocialPost s, string why, CancellationToken ct)
    {
        s.Status = SocialPostStatus.Failed;
        s.LastError = why.Length > 1000 ? why[..1000] : why;
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Posting product {Product} to {Platform} failed: {Why}", s.ProductId, s.Platform, why);
    }

    private async Task NotifyAsync(string subject, string html)
    {
        try { await email.AdminsAsync(Email.EmailEvent.AdminSocialProblem, subject, subject, html, ("Open social media", email.AdminUrl + "/social")); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not email the social media problem"); }
    }

    /// <summary>Instagram needs JPEG photos at public addresses: each product photo becomes a 1080×1350 JPEG on white
    /// (the whole product visible, never cropped), named after its source so it's made only once.</summary>
    private List<string> PrepareJpegs(int productId, List<string> sources)
    {
        var folder = $"/media/social/{productId}";
        Directory.CreateDirectory(storage.FullPath(folder));
        var made = new List<string>();
        foreach (var src in sources)
        {
            var name = Path.GetFileNameWithoutExtension(src);
            var rel = $"{folder}/{name}.jpg";
            var full = storage.FullPath(rel);
            if (!File.Exists(full))
            {
                using var bmp = SKBitmap.Decode(storage.FullPath(src)) ?? throw new IOException($"Can't read photo {src}");
                using var surface = SKSurface.Create(new SKImageInfo(PhotoWidth, PhotoHeight));
                surface.Canvas.Clear(SKColors.White);
                var scale = Math.Min((float)PhotoWidth / bmp.Width, (float)PhotoHeight / bmp.Height);
                var w = bmp.Width * scale; var h = bmp.Height * scale;
                using var img = SKImage.FromBitmap(bmp);
                surface.Canvas.DrawImage(img, SKRect.Create((PhotoWidth - w) / 2, (PhotoHeight - h) / 2, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
                using var snapshot = surface.Snapshot();
                using var jpg = snapshot.Encode(SKEncodedImageFormat.Jpeg, 90);
                using var fs = File.Create(full);
                jpg.SaveTo(fs);
            }
            made.Add(rel);
        }
        // Remove JPEGs of photos the product no longer uses.
        var keep = made.Select(Path.GetFileName).ToHashSet();
        foreach (var f in Directory.GetFiles(storage.FullPath(folder), "*.jpg"))
            if (!keep.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch (IOException) { }
        return made;
    }

    private string? PublicBase() => urls.Value.ApiUrl is { Length: > 0 } u && u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? u.TrimEnd('/') : null;

    private async Task<SocialPost> Post(int id) => await db.SocialPosts.FirstOrDefaultAsync(s => s.Id == id) ?? throw new AppException("Post not found", 404);

    private static List<TimeSpan> ParseTimes(string? s) => (s ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
        .Select(t => TimeSpan.TryParse(t, out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1) ? ts : (TimeSpan?)null)
        .Where(t => t is not null).Select(t => new TimeSpan(t!.Value.Hours, t.Value.Minutes, 0)).Distinct().Order().ToList();

    private static readonly TimeZoneInfo India = FindIndia();
    private static TimeZoneInfo FindIndia()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "India", "India");
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
