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
    bool AutoQueueNewProducts, bool PublicPhotos,
    bool ThreadsConnected, string? ThreadsAppId, string? ThreadsUsername, DateTime? ThreadsConnectedAt, DateTime? ThreadsTokenExpiresAt,
    bool ThreadsEnabled, string ThreadsTemplate, bool ThreadsCustomTemplate, string ThreadsDefaultTemplate, string? ThreadsTopic, string? ThreadsCallbackUrl,
    bool ReelsEnabled, string ReelTimes, bool ReelMusic, bool ReelsAvailable, List<SocialMusicTrack> MusicTracks);
public record SocialMusicTrack(string Name, long Size);
public record SocialSettingsUpdate(bool InstagramEnabled, bool FacebookEnabled, string? Template, bool IncludePrice, bool ShowMrp,
    string? WhatsApp, string? City, string? Hashtags, int MaxHashtags, string PostTimes, bool AutoQueueNewProducts,
    bool ThreadsEnabled = true, string? ThreadsTemplate = null, string? ThreadsTopic = "Furniture",
    bool ReelsEnabled = false, string? ReelTimes = "18:00", bool ReelMusic = true);
public record ThreadsConnectRequest(string AppId, string AppSecret);
public record SocialConnectRequest(string AppId, string AppSecret, string UserToken, string? PageId);
public record SocialPageChoice(string Id, string Name, string? Instagram);
public record SocialConnectResult(bool Connected, List<SocialPageChoice>? Pages, string? Message);
public record SocialQueueRequest(List<int> ProductIds, bool Now = false, List<SocialPlatform>? Platforms = null);
public record SocialQueueResult(int Products, int PostingNow, int Scheduled, int Skipped, DateTime? FirstSlot, string? Note);
public record SocialPlatformState(SocialPlatform Platform, bool Enabled, int? PostId, SocialPostStatus? Status, DateTime? ScheduledAt,
    DateTime? PublishedAt, string? Permalink, string? LastError, List<string> Changes, string? PostedCaption, bool CustomCaption);
public record SocialProductDto(int ProductId, string Caption, string Code, List<string> Photos, bool Connected, bool Publishable, List<SocialPlatformState> Platforms,
    string ThreadsCaption);
public record SocialPostRow(int Id, int ProductId, string ProductName, string? Image, SocialPlatform Platform, SocialPostStatus Status,
    DateTime? ScheduledAt, DateTime? PublishedAt, string? Permalink, string? LastError, List<string> Changes, bool CustomCaption);
public record SocialQuota(SocialPlatform Platform, int Used, int Limit, bool FromMeta);

/// <summary>
/// Posts products to Instagram, the Facebook Page and Threads: one post per product per platform, at a steady pace (Admin →
/// Social media). Facebook posts follow product changes automatically; Instagram and Threads can't edit posts through the
/// API, so a changed product is marked "out of date" for the admin to copy the new caption or replace the post.
/// </summary>
public class SocialService(AppDbContext db, MetaClient meta, ThreadsClient threads, ReelMaker reels, IDataProtectionProvider dp, FileStorage storage, IMemoryCache cache,
    IOptions<SmtpOptions> urls, EmailSender email, ILogger<SocialService> logger)
{
    public const int PhotoWidth = 1080, PhotoHeight = 1350;     // 4:5 portrait: the most screen space in the feed
    private readonly IDataProtector _protector = dp.CreateProtector("WoodsyCart.SocialSecrets.v1");
    private static readonly SocialPostStatus[] Live = [SocialPostStatus.Published, SocialPostStatus.OutOfDate];
    private static readonly SocialPostStatus[] Taken = [SocialPostStatus.Queued, SocialPostStatus.Publishing, SocialPostStatus.Published, SocialPostStatus.OutOfDate];
    private static readonly SocialPlatform[] Reels = [SocialPlatform.InstagramReel, SocialPlatform.FacebookReel, SocialPlatform.ThreadsVideo];
    private static readonly SocialPlatform[] ThreadsKinds = [SocialPlatform.Threads, SocialPlatform.ThreadsVideo];
    private static bool IsThreads(SocialPlatform p) => ThreadsKinds.Contains(p);
    private static readonly SocialPlatform[] InstagramKinds = [SocialPlatform.Instagram, SocialPlatform.InstagramReel];
    private static bool IsReel(SocialPlatform p) => Reels.Contains(p);

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

    public Task SaveConfigAsync() => db.SaveChangesAsync();

    public async Task<SocialSettingsDto> SettingsAsync()
    {
        var c = await ConfigAsync();
        return new SocialSettingsDto(IsConnected(c), c.MetaAppId, c.PageId, c.PageName, c.InstagramUserId, c.InstagramUsername, c.ConnectedAt,
            c.InstagramEnabled, c.FacebookEnabled, c.Template ?? SocialCaption.DefaultTemplate, c.Template is not null, SocialCaption.DefaultTemplate,
            SocialCaption.Tokens, c.IncludePrice, c.ShowMrp, c.WhatsApp, c.City, c.Hashtags, c.MaxHashtags, c.PostTimes, c.AutoQueueNewProducts,
            PublicBase() is not null,
            ThreadsConnected(c), c.ThreadsAppId, c.ThreadsUsername, c.ThreadsConnectedAt, c.ThreadsTokenExpiresAt, c.ThreadsEnabled,
            c.ThreadsTemplate ?? SocialCaption.DefaultThreadsTemplate, c.ThreadsTemplate is not null, SocialCaption.DefaultThreadsTemplate,
            c.ThreadsTopic, ThreadsCallback(),
            c.ReelsEnabled, c.ReelTimes, c.ReelMusic, reels.Available, Music());
    }

    public async Task SaveSettingsAsync(SocialSettingsUpdate r)
    {
        var times = ParseTimes(r.PostTimes);
        if (times.Count == 0) throw new AppException("Enter at least one posting time, e.g. 11:00, 15:00, 20:30.");
        if (r.MaxHashtags is < 1 or > 30) throw new AppException("Hashtags per post must be between 1 and 30 (Instagram allows 30; 3–5 work best).");
        var template = string.IsNullOrWhiteSpace(r.Template) || r.Template.Replace("\r", "").Trim() == SocialCaption.DefaultTemplate.Trim() ? null : r.Template.Trim();
        if (template is not null && !template.Contains("{name}")) throw new AppException("The caption template must contain {name}.");
        var threadsTemplate = string.IsNullOrWhiteSpace(r.ThreadsTemplate) || r.ThreadsTemplate.Replace("\r", "").Trim() == SocialCaption.DefaultThreadsTemplate.Trim()
            ? null : r.ThreadsTemplate.Trim();
        if (threadsTemplate is not null && !threadsTemplate.Contains("{name}")) throw new AppException("The Threads template must contain {name}.");
        var topic = Clean(r.ThreadsTopic)?.TrimStart('#');
        if (topic is { Length: > 50 } || topic?.IndexOfAny(['.', '&']) >= 0)
            throw new AppException("The Threads topic can be at most 50 characters, without full stops or &.");
        var reelTimes = ParseTimes(r.ReelTimes);
        if (r.ReelsEnabled && reelTimes.Count == 0) throw new AppException("Enter at least one reel time, e.g. 18:00.");
        var c = await ConfigAsync();
        if (r.AutoQueueNewProducts && !c.AutoQueueNewProducts) c.AutoQueueSince = DateTime.UtcNow;
        c.InstagramEnabled = r.InstagramEnabled; c.FacebookEnabled = r.FacebookEnabled; c.Template = template;
        c.IncludePrice = r.IncludePrice; c.ShowMrp = r.ShowMrp; c.WhatsApp = Clean(r.WhatsApp); c.City = Clean(r.City); c.Hashtags = Clean(r.Hashtags);
        c.MaxHashtags = r.MaxHashtags; c.PostTimes = string.Join(",", times.Select(t => t.ToString(@"hh\:mm")));
        c.AutoQueueNewProducts = r.AutoQueueNewProducts;
        c.ThreadsEnabled = r.ThreadsEnabled; c.ThreadsTemplate = threadsTemplate; c.ThreadsTopic = topic;
        c.ReelsEnabled = r.ReelsEnabled; c.ReelMusic = r.ReelMusic;
        if (reelTimes.Count > 0) c.ReelTimes = string.Join(",", reelTimes.Select(t => t.ToString(@"hh\:mm")));
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

    /// <summary>Step 1 of connecting Threads: remembers the app ID and secret for 15 minutes (in memory only) and returns
    /// the Threads login address. Threads sends the browser back to <see cref="ThreadsCallbackAsync"/>.</summary>
    public string StartThreads(ThreadsConnectRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.AppId) || string.IsNullOrWhiteSpace(r.AppSecret))
            throw new AppException("Enter the Threads app ID and Threads app secret (Meta app → App settings → Basic).");
        var callback = ThreadsCallback() ?? throw new AppException("Threads needs the API's public https address (Smtp:ApiUrl).");
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        cache.Set(ThreadsStateKey + state, (r.AppId.Trim(), r.AppSecret.Trim()), TimeSpan.FromMinutes(15));
        return ThreadsClient.AuthorizeUrl(r.AppId.Trim(), callback, state);
    }

    /// <summary>Step 2: Threads' redirect. Swaps the code for a 60-day token and returns where to send the browser in Admin.</summary>
    public async Task<string> ThreadsCallbackAsync(string? code, string? state, string? error)
    {
        string Back(string query) => $"{email.AdminUrl}/social?tab=settings&{query}";
        if (!string.IsNullOrEmpty(error)) return Back("threads=cancelled");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || !cache.TryGetValue(ThreadsStateKey + state, out (string AppId, string Secret) app))
            return Back("threads=expired");
        cache.Remove(ThreadsStateKey + state);
        try
        {
            var (token, userId, username) = await threads.ConnectAsync(app.AppId, app.Secret, ThreadsCallback()!, code.Split('#')[0]);
            var c = await ConfigAsync();
            c.ThreadsAppId = app.AppId; c.ThreadsUserId = userId; c.ThreadsUsername = username;
            c.ThreadsTokenProtected = _protector.Protect(token.Token); c.ThreadsTokenExpiresAt = token.ExpiresAt;
            c.ThreadsTokenRefreshedAt = DateTime.UtcNow; c.ThreadsConnectedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Back("threads=connected");
        }
        catch (Exception ex) when (ex is MetaException or HttpRequestException)
        {
            logger.LogWarning("Connecting Threads failed: {Message}", ex.Message);
            return Back("threads=failed&reason=" + Uri.EscapeDataString(ex.Message));
        }
    }

    public async Task DisconnectThreadsAsync()
    {
        var c = await ConfigAsync();
        c.ThreadsTokenProtected = null; c.ThreadsConnectedAt = null; c.ThreadsTokenExpiresAt = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Threads tokens last 60 days: get a fresh one weekly (worker). An expired token can only be replaced by connecting again.</summary>
    public async Task RefreshThreadsTokenAsync(CancellationToken ct)
    {
        var c = await ConfigAsync();
        if (!ThreadsConnected(c) || c.ThreadsTokenRefreshedAt > DateTime.UtcNow.AddDays(-7)) return;
        try
        {
            var t = await threads.RefreshAsync(ThreadsToken(c), ct);
            c.ThreadsTokenProtected = _protector.Protect(t.Token); c.ThreadsTokenExpiresAt = t.ExpiresAt; c.ThreadsTokenRefreshedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is MetaException or HttpRequestException)
        {
            c.ThreadsTokenRefreshedAt = DateTime.UtcNow.AddDays(-6);   // try again tomorrow
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Refreshing the Threads token failed: {Message}", ex.Message);
            if (ex is MetaException { TokenInvalid: true } || c.ThreadsTokenExpiresAt < DateTime.UtcNow.AddDays(7))
                await NotifyAsync("Threads needs connecting again",
                    $"<p>WoodsyCart couldn't renew its Threads access ({System.Net.WebUtility.HtmlEncode(ex.Message)}). It runs out on {c.ThreadsTokenExpiresAt:d MMM yyyy}.</p><p>Connect Threads again in Admin → Social media → Settings.</p>");
        }
    }

    private const string ThreadsStateKey = "social:threads-state:";
    private string? ThreadsCallback() => PublicBase() is { } b ? b + "/api/social/threads/callback" : null;

    /// <summary>Facebook Page (and Instagram) connected.</summary>
    private static bool IsConnected(SocialConfig c) => c.PageTokenProtected is not null && c.PageId is not null;
    private static bool ThreadsConnected(SocialConfig c) => c.ThreadsTokenProtected is not null && c.ThreadsUserId is not null;
    private static bool AnyConnected(SocialConfig c) => IsConnected(c) || ThreadsConnected(c);
    private string Token(SocialConfig c) => _protector.Unprotect(c.PageTokenProtected!);
    private string ThreadsToken(SocialConfig c) => _protector.Unprotect(c.ThreadsTokenProtected!);

    private static List<SocialPlatform> Enabled(SocialConfig c)
    {
        var list = new List<SocialPlatform>();
        if (c.InstagramEnabled && IsConnected(c) && c.InstagramUserId is not null) list.Add(SocialPlatform.Instagram);
        if (c.FacebookEnabled && IsConnected(c)) list.Add(SocialPlatform.Facebook);
        if (c.ThreadsEnabled && ThreadsConnected(c)) list.Add(SocialPlatform.Threads);
        // Reels can always be posted from a product; Settings → Reels only decides whether bulk and automatic posting add them
        if (list.Contains(SocialPlatform.Instagram)) list.Add(SocialPlatform.InstagramReel);
        if (list.Contains(SocialPlatform.Facebook)) list.Add(SocialPlatform.FacebookReel);
        if (list.Contains(SocialPlatform.Threads)) list.Add(SocialPlatform.ThreadsVideo);
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

    private async Task<PostContent> ContentAsync(Product p, SocialConfig c, string? overrideCaption, SocialPlatform platform, BusinessSettings? biz = null) =>
        SocialCaption.Build(p, c, biz ?? await db.BusinessSettings.AsNoTracking().FirstAsync(), overrideCaption, Photos(p),
            IsThreads(platform), email.StorefrontUrl);

    public async Task<SocialProductDto> ProductAsync(int productId)
    {
        var p = await WithDetails().FirstOrDefaultAsync(x => x.Id == productId) ?? throw new AppException("Product not found", 404);
        // Check this product's posts now instead of waiting for the 10-minute run, so the card shows the current status
        await RefreshAsync([productId]);
        var c = await ConfigAsync();
        var posts = await db.SocialPosts.AsNoTracking().Where(s => s.ProductId == productId).ToListAsync();
        var biz = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var plain = await ContentAsync(p, c, null, SocialPlatform.Instagram, biz);
        var threadsPlain = await ContentAsync(p, c, null, SocialPlatform.Threads, biz);
        var enabled = Enabled(c);
        var shown = new List<SocialPlatform> { SocialPlatform.Instagram, SocialPlatform.Facebook };
        if (ThreadsConnected(c) || posts.Any(x => x.Platform == SocialPlatform.Threads)) shown.Add(SocialPlatform.Threads);
        foreach (var reel in Reels)
            if ((reel == SocialPlatform.ThreadsVideo ? ThreadsConnected(c) : IsConnected(c)) || posts.Any(x => x.Platform == reel)) shown.Add(reel);
        var platforms = shown.Select(pl =>
        {
            var s = posts.FirstOrDefault(x => x.Platform == pl);
            var basic = IsThreads(pl) ? threadsPlain : plain;
            var now = s?.CaptionOverride is null ? basic
                : SocialCaption.Build(p, c, biz, s.CaptionOverride, plain.Photos, IsThreads(pl), email.StorefrontUrl);
            var changes = s is { Status: SocialPostStatus.OutOfDate } ? SocialCaption.Changes(s.PostedSnapshot, now.Snapshot) : [];
            return new SocialPlatformState(pl, enabled.Contains(pl), s?.Id, s?.Status, s?.ScheduledAt, s?.PublishedAt, s?.Permalink, s?.LastError,
                changes, s?.PostedCaption, s?.CaptionOverride is not null);
        }).ToList();
        return new SocialProductDto(p.Id, plain.Caption, SocialCaption.Code(p.Id), plain.Photos, AnyConnected(c),
            p.IsActive && plain.Photos.Count > 0, platforms, threadsPlain.Caption);
    }

    /// <summary>Makes (or returns) the product's reel video: for downloading and posting by hand with a trending song
    /// (<paramref name="music"/> false), or with one of the uploaded tracks.</summary>
    public async Task<ReelFile> MakeReelAsync(int productId, bool music, bool remake = false)
    {
        var p = await WithDetails().FirstOrDefaultAsync(x => x.Id == productId) ?? throw new AppException("Product not found", 404);
        var biz = await db.BusinessSettings.AsNoTracking().FirstAsync();
        return await reels.MakeAsync(p.Id, SocialCaption.Reel(p, await ConfigAsync(), biz), Photos(p), music, remake: remake);
    }

    public string ReelPath(ReelFile r) => storage.FullPath(r.Video);

    // ---------- Reel music (royalty-free tracks uploaded in Admin) ----------

    public List<SocialMusicTrack> Music() => reels.MusicTracks()
        .Select(n => new SocialMusicTrack(n, new FileInfo(storage.FullPath($"{ReelMaker.MusicFolder}/{n}")).Length)).ToList();

    public async Task<List<SocialMusicTrack>> AddMusicAsync(Stream content, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!ReelMaker.MusicTypes.Contains(ext)) throw new AppException("Upload an MP3, M4A, AAC or WAV file.");
        var name = string.Concat(Path.GetFileNameWithoutExtension(fileName).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ' ' ? ch : '-')).Trim();
        if (name.Length == 0) name = "track";
        if (name.Length > 60) name = name[..60];
        Directory.CreateDirectory(storage.FullPath(ReelMaker.MusicFolder));
        await using (var fs = File.Create(storage.FullPath($"{ReelMaker.MusicFolder}/{name}{ext}"))) await content.CopyToAsync(fs);
        return Music();
    }

    public List<SocialMusicTrack> DeleteMusic(string name)
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) throw new AppException("Not a track name.");
        var path = storage.FullPath($"{ReelMaker.MusicFolder}/{name}");
        if (File.Exists(path)) File.Delete(path);
        return Music();
    }

    /// <summary>The caption the post should have now (custom caption if one was saved).</summary>
    public async Task<string> CurrentCaptionAsync(int postId)
    {
        var s = await db.SocialPosts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == postId) ?? throw new AppException("Post not found", 404);
        var p = await WithDetails().FirstAsync(x => x.Id == s.ProductId);
        return (await ContentAsync(p, await ConfigAsync(), s.CaptionOverride, s.Platform)).Caption;
    }

    // ---------- Queue ----------

    /// <summary>Adds products to the queue. "Now" posts as many as today's limit allows straight away; the rest (or all,
    /// when not "now") get the next free posting times. A product already posted or queued on a platform is skipped there.</summary>
    public async Task<SocialQueueResult> QueueAsync(SocialQueueRequest r)
    {
        var c = await ConfigAsync();
        if (!AnyConnected(c)) throw new AppException("Connect Instagram and Facebook, or Threads, first (Social media → Settings).");
        var platforms = Enabled(c).Where(p => r.Platforms is null ? !IsReel(p) || c.ReelsEnabled : r.Platforms.Contains(p)).ToList();
        if (platforms.Count == 0) throw new AppException("Switch on Instagram, Facebook or Threads in Social media settings.");

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
        // Photo posts and reels each have their own posting times. A product already waiting in the queue on another platform
        // of the same kind (e.g. Threads added later) goes out at that same time.
        var waiting = existing.Where(s => s.Status == SocialPostStatus.Queued && s.ScheduledAt is not null)
            .GroupBy(s => (s.ProductId, IsReel(s.Platform))).ToDictionary(g => g.Key, g => g.Min(s => s.ScheduledAt!.Value));
        int Needed(bool reel) => work.Skip(nowCount).Count(w => w.Platforms.Any(pl => IsReel(pl) == reel) && !waiting.ContainsKey((w.ProductId, reel)));
        var photoSlots = await FreeSlotsAsync(c, Needed(false), reel: false);
        var reelSlots = await FreeSlotsAsync(c, Needed(true), reel: true);
        int nextPhoto = 0, nextReel = 0, joined = 0;
        var at = DateTime.UtcNow;
        for (var i = 0; i < work.Count; i++)
        {
            var times = new Dictionary<bool, DateTime>();
            foreach (var pl in work[i].Platforms)
            {
                var reel = IsReel(pl);
                if (!times.TryGetValue(reel, out var when))
                {
                    if (i < nowCount) when = at;
                    else if (waiting.TryGetValue((work[i].ProductId, reel), out var t)) { when = t; joined++; }
                    else when = reel ? reelSlots[nextReel++] : photoSlots[nextPhoto++];
                    times[reel] = when;
                }
                var s = existing.FirstOrDefault(x => x.ProductId == work[i].ProductId && x.Platform == pl);
                if (s is null) { s = new SocialPost { ProductId = work[i].ProductId, Platform = pl }; db.SocialPosts.Add(s); }
                s.Status = SocialPostStatus.Queued; s.ScheduledAt = when; s.LastError = null;
            }
        }
        await db.SaveChangesAsync();
        if (joined > 0) note = (note is null ? "" : note + " ") + $"{joined} already in the queue on another platform will post at that same time.";
        var first = photoSlots.Concat(reelSlots).Order().Cast<DateTime?>().FirstOrDefault();
        return new SocialQueueResult(work.Count, nowCount, work.Count - nowCount, skipped, first, note);
    }

    /// <summary>How many more products can be posted in the next 24 hours (Instagram is the limiting platform).</summary>
    private async Task<int> RoomNowAsync(SocialConfig c, List<SocialPlatform> platforms)
    {
        if (!platforms.Any(InstagramKinds.Contains)) return int.MaxValue;
        var q = await QuotaAsync(c);
        var dueAlready = await db.SocialPosts.CountAsync(s => InstagramKinds.Contains(s.Platform)
            && (s.Status == SocialPostStatus.Publishing || (s.Status == SocialPostStatus.Queued && s.ScheduledAt <= DateTime.UtcNow)));
        // Photo post + reel of the same product both count against Instagram's limit
        var perProduct = platforms.Count(InstagramKinds.Contains);
        return Math.Max(0, (q.Limit - q.Used - dueAlready) / perProduct);
    }

    private const string QuotaKey = "social:ig-quota";

    /// <summary>Instagram's own counter when reachable (cached briefly), otherwise our own 24-hour count against 100.</summary>
    public async Task<SocialQuota> QuotaAsync(SocialConfig? cfg = null, bool fresh = false)
    {
        var c = cfg ?? await ConfigAsync();
        var since = DateTime.UtcNow.AddHours(-24);
        var ours = await db.SocialPosts.CountAsync(s => InstagramKinds.Contains(s.Platform) && s.PublishedAt >= since);
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

    /// <summary>Threads' own 24-hour counter (250 a day), or our own count when Threads can't be reached.</summary>
    private async Task<SocialQuota> ThreadsQuotaAsync(SocialConfig c)
    {
        try
        {
            var (used, limit) = await threads.QuotaAsync(c.ThreadsUserId!, ThreadsToken(c));
            return new SocialQuota(SocialPlatform.Threads, used, limit, true);
        }
        catch (Exception ex) when (ex is MetaException or HttpRequestException)
        {
            logger.LogWarning("Could not read Threads' publishing limit: {Message}", ex.Message);
            var since = DateTime.UtcNow.AddHours(-24);
            return new SocialQuota(SocialPlatform.Threads, await db.SocialPosts.CountAsync(s => ThreadsKinds.Contains(s.Platform) && s.PublishedAt >= since), 250, false);
        }
    }

    /// <summary>The next posting times (from Settings, India time) that no queued product holds yet. Reels have their own times.</summary>
    private async Task<List<DateTime>> FreeSlotsAsync(SocialConfig c, int count, bool reel = false)
    {
        var result = new List<DateTime>();
        if (count <= 0) return result;
        var now = DateTime.UtcNow;
        var taken = (await db.SocialPosts.Where(s => s.Status == SocialPostStatus.Queued && s.ScheduledAt > now && Reels.Contains(s.Platform) == reel)
                .Select(s => s.ScheduledAt!.Value).ToListAsync())
            .Select(d => new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, 0, DateTimeKind.Utc)).ToHashSet();
        var times = ParseTimes(reel ? c.ReelTimes : c.PostTimes);
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
            if (!(IsThreads(s.Platform) ? ThreadsConnected(c) : IsConnected(c))) throw new AppException($"Connect {s.Platform} in Settings first.");
            try { await DeleteExternalAsync(s, c); }
            catch (MetaException ex) when (ex.Code is 100) { }   // already deleted in the app
            catch (MetaException ex) when (ex.Code is 10 or 200 && s.Platform is SocialPlatform.Instagram or SocialPlatform.Threads or SocialPlatform.ThreadsVideo)
            {
                throw new AppException(s.Platform == SocialPlatform.Instagram
                    ? "Instagram needs the instagram_manage_contents permission to delete posts. Add it to the Meta app, then reconnect in Settings with a new token."
                    : "Threads needs the threads_delete permission to delete posts. Add it to the Threads use case, then connect Threads again.");
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
        s.ScheduledAt = !InstagramKinds.Contains(s.Platform) || await RoomNowAsync(c, [s.Platform]) > 0
            ? DateTime.UtcNow : (await FreeSlotsAsync(c, 1, IsReel(s.Platform)))[0];
        await db.SaveChangesAsync();
    }

    /// <summary>Saves a custom caption for this post (empty = back to the template). A live Facebook post is updated straight away.</summary>
    public async Task SetCaptionAsync(int postId, string? caption)
    {
        var s = await Post(postId);
        s.CaptionOverride = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (s.CaptionOverride?.Length > SocialCaption.MaxLength) throw new AppException($"Captions can be at most {SocialCaption.MaxLength} characters.");
        if (IsThreads(s.Platform) && s.CaptionOverride is not null && SocialCaption.ThreadsLength(s.CaptionOverride) > SocialCaption.ThreadsMaxLength)
            throw new AppException($"Threads allows {SocialCaption.ThreadsMaxLength} characters (emojis and ₹ count as 3–4). Shorten the text.");
        await db.SaveChangesAsync();
        if (s.Platform == SocialPlatform.Facebook && Live.Contains(s.Status)) await RefreshAsync([s.ProductId]);
    }

    /// <summary>After pasting the new caption into the Instagram app: the post counts as up to date again.</summary>
    public async Task MarkUpdatedAsync(int postId)
    {
        var s = await Post(postId);
        if (!Live.Contains(s.Status)) throw new AppException("This post isn't live.");
        var p = await WithDetails().FirstAsync(x => x.Id == s.ProductId);
        Apply(s, await ContentAsync(p, await ConfigAsync(), s.CaptionOverride, s.Platform));
        s.Status = SocialPostStatus.Published;
        await db.SaveChangesAsync();
    }

    /// <summary>Deletes the live post and posts the product again (likes and comments on the old post are lost).</summary>
    public async Task ReplaceAsync(int postId)
    {
        var s = await Post(postId);
        if (!Live.Contains(s.Status)) throw new AppException("Only live posts can be replaced.");
        var c = await ConfigAsync();
        if (InstagramKinds.Contains(s.Platform) && await RoomNowAsync(c, [s.Platform]) == 0)
            throw new AppException("Instagram's daily limit is used up. Replace the post tomorrow.");
        if (s.ExternalId is not null)
        {
            try { await DeleteExternalAsync(s, c); }
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
                    changes[r.Post.Id] = SocialCaption.Changes(r.Post.PostedSnapshot, (await ContentAsync(p, c, r.Post.CaptionOverride, r.Post.Platform, biz)).Snapshot);
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
            // Names only for connections that work now (a disconnected Page keeps its name in the settings)
            connected = AnyConnected(c), instagram = IsConnected(c) ? c.InstagramUsername : null, page = IsConnected(c) ? c.PageName : null,
            threads = ThreadsConnected(c) ? c.ThreadsUsername : null,
            enabled = Enabled(c), quota = await QuotaAsync(c), threadsQuota = ThreadsConnected(c) ? await ThreadsQuotaAsync(c) : null,
            facebookLast24h = fb24, nextPostAt = next,
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
        if (!AnyConnected(c)) return 0;
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
        if (s.Platform == SocialPlatform.FacebookReel
            && await db.SocialPosts.CountAsync(x => x.Platform == SocialPlatform.FacebookReel && x.PublishedAt >= DateTime.UtcNow.AddHours(-24), ct) >= 30)
        {
            s.ScheduledAt = (await FreeSlotsAsync(c, 1, reel: true))[0];
            s.LastError = "Facebook's limit of 30 reels in 24 hours was reached; moved to the next reel time.";
            await db.SaveChangesAsync(ct);
            return;
        }
        if (InstagramKinds.Contains(s.Platform))
        {
            var q = await QuotaAsync(c, fresh: true);
            if (q.Used >= q.Limit)
            {
                s.ScheduledAt = (await FreeSlotsAsync(c, 1, IsReel(s.Platform)))[0];
                s.LastError = $"Instagram's limit of {q.Limit} posts in 24 hours was reached; moved to the next posting time.";
                await db.SaveChangesAsync(ct);
                return;
            }
        }
        if (IsThreads(s.Platform))
        {
            try
            {
                var (used, limit) = await threads.QuotaAsync(c.ThreadsUserId!, ThreadsToken(c), ct);
                if (used >= limit)
                {
                    s.ScheduledAt = (await FreeSlotsAsync(c, 1))[0];
                    s.LastError = $"Threads' limit of {limit} posts in 24 hours was reached; moved to the next posting time.";
                    await db.SaveChangesAsync(ct);
                    return;
                }
            }
            // Not knowing the limit doesn't stop the post; an invalid token fails it below, which emails the admins.
            catch (Exception ex) when (ex is MetaException or HttpRequestException) { logger.LogWarning("Could not read Threads' publishing limit: {Message}", ex.Message); }
        }

        var content = await ContentAsync(p, c, s.CaptionOverride, s.Platform);
        if (content.Photos.Count == 0) { await FailAsync(s, "The product has no photos.", ct); return; }
        if (PublicBase() is not { } baseUrl) { await FailAsync(s, "Photos need a public address: set Smtp:ApiUrl (e.g. https://api.woodsycart.com).", ct); return; }

        s.Status = SocialPostStatus.Publishing; s.Attempts++;
        await db.SaveChangesAsync(ct);
        try
        {
            PublishResult result;
            if (IsReel(s.Platform))
            {
                var biz = await db.BusinessSettings.AsNoTracking().FirstAsync(ct);
                var reel = await reels.MakeAsync(p.Id, SocialCaption.Reel(p, c, biz), content.Photos, c.ReelMusic, ct);
                result = s.Platform switch
                {
                    SocialPlatform.InstagramReel => await meta.PublishInstagramReelAsync(c.InstagramUserId!, Token(c), baseUrl + reel.Video, baseUrl + reel.Cover, content.Caption, ct),
                    SocialPlatform.FacebookReel => await meta.PublishFacebookReelAsync(c.PageId!, Token(c), storage.FullPath(reel.Video), content.Caption, ct),
                    _ => await threads.PublishVideoAsync(c.ThreadsUserId!, ThreadsToken(c), baseUrl + reel.Video, content.Caption, c.ThreadsTopic, ct)
                };
            }
            else
            {
                var urls = PrepareJpegs(p.Id, content.Photos).Select(rel => baseUrl + rel).ToList();
                result = s.Platform switch
                {
                    SocialPlatform.Instagram => await meta.PublishInstagramAsync(c.InstagramUserId!, Token(c), urls, content.Caption, ct),
                    SocialPlatform.Facebook => await meta.PublishFacebookAsync(c.PageId!, Token(c), urls, content.Caption, ct),
                    _ => await threads.PublishAsync(c.ThreadsUserId!, ThreadsToken(c), urls, content.Caption, c.ThreadsTopic, ct)
                };
            }
            s.ExternalId = result.Id; s.Permalink = result.Permalink; s.PublishedAt = DateTime.UtcNow;
            s.Status = SocialPostStatus.Published; s.LastError = null;
            Apply(s, content);
            await db.SaveChangesAsync(ct);
            cache.Remove(QuotaKey);
            logger.LogInformation("Posted product {Product} to {Platform}: {Link}", p.Id, s.Platform, result.Permalink);
        }
        catch (Exception ex) when (ex is MetaException or HttpRequestException or IOException or AppException)
        {
            await FailAsync(s, ex.Message, ct);
            if (ex is MetaException { TokenInvalid: true })
                await NotifyAsync(IsThreads(s.Platform) ? "Threads is disconnected" : "Instagram and Facebook are disconnected",
                    IsThreads(s.Platform)
                        ? "<p>Threads no longer accepts WoodsyCart's access (the token expired or was removed). Threads posts are failing.</p><p>Connect Threads again in Admin → Social media → Settings.</p>"
                        : "<p>Meta no longer accepts WoodsyCart's access (the Page token was revoked or the password changed). Posting is paused.</p><p>Connect again in Admin → Social media → Settings.</p>");
        }
    }

    /// <summary>Compares live posts with their products: Facebook posts are updated, Instagram posts marked out of date.</summary>
    public async Task<int> RefreshAsync(IReadOnlyCollection<int>? productIds = null, CancellationToken ct = default)
    {
        var c = await ConfigAsync();
        if (!AnyConnected(c)) return 0;
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
            var content = await ContentAsync(p, c, s.CaptionOverride, s.Platform, biz);
            if (content.Hash == s.ContentHash)
            {
                if (s.Status == SocialPostStatus.OutOfDate) { s.Status = SocialPostStatus.Published; changed++; }
                continue;
            }
            var before = string.IsNullOrEmpty(s.PostedSnapshot) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(s.PostedSnapshot);
            var samePhotos = before?.GetValueOrDefault("Photos") == content.Snapshot["Photos"];
            if (s.Platform == SocialPlatform.Facebook && samePhotos && s.ExternalId is not null && IsConnected(c))
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
        if (!AnyConnected(c) || !c.AutoQueueNewProducts || c.AutoQueueSince is null) return 0;
        var ids = await db.Products.Where(p => p.IsActive && p.CreatedAt >= c.AutoQueueSince
                && p.Images.Any(i => i.MediaType == MediaType.Image && i.LocalPath != null)
                && !db.SocialPosts.Any(s => s.ProductId == p.Id))
            .OrderBy(p => p.Id).Select(p => p.Id).Take(200).ToListAsync(ct);
        if (ids.Count == 0) return 0;
        return (await QueueAsync(new SocialQueueRequest(ids))).Products;
    }

    // ---------- Helpers ----------

    private Task DeleteExternalAsync(SocialPost s, SocialConfig c) => IsThreads(s.Platform)
        ? threads.DeleteAsync(s.ExternalId!, ThreadsToken(c))
        : meta.DeleteAsync(s.ExternalId!, Token(c));

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
    /// <summary>The product's post/feed JPEGs if they are all made already, else null.</summary>
    public List<string>? ReadyJpegs(Product p)
    {
        var rel = Photos(p).Select(src => $"/media/social/{p.Id}/{Path.GetFileNameWithoutExtension(src)}.jpg").ToList();
        return rel.Count > 0 && rel.All(r => File.Exists(storage.FullPath(r))) ? rel : null;
    }

    public List<string> PrepareJpegsFor(Product p) => PrepareJpegs(p.Id, Photos(p));

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
        // Remove JPEGs of photos the product no longer uses (reel covers are ReelMaker's).
        var keep = made.Select(Path.GetFileName).ToHashSet();
        foreach (var f in Directory.GetFiles(storage.FullPath(folder), "*.jpg"))
            if (!keep.Contains(Path.GetFileName(f)) && !Path.GetFileName(f).StartsWith("reel-")) try { File.Delete(f); } catch (IOException) { }
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
