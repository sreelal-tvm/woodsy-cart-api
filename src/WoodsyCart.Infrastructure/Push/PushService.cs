using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Email;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Push;

public record PushKeysDto(string P256dh, string Auth);
public record PushSubscribeRequest(string Endpoint, PushKeysDto Keys);
/// <summary>Storefront: turn on notifications on this device — from the app's prompt, or from an order (guests: order number + its link token).</summary>
public record CustomerPushRequest(string Endpoint, PushKeysDto Keys, string? OrderNumber, string? Token, bool? Promotions);
public record PushDeviceDto(int Id, string? Device, DateTime CreatedAt, DateTime? LastSentAt, string? LastError, bool ThisDevice);
public record PushStatusDto(string PublicKey, bool ThisDevice, List<PushDeviceDto> Devices);
public record CustomerPushStatusDto(string PublicKey, bool On, bool Enabled, bool Promotions);
public record PushPreferencesRequest(string Endpoint, bool Promotions);
public record PushCartRequest(string Endpoint, int Count, int? ProductId);
public record PushSettingsDto(bool CustomersEnabled, bool TeamEnabled, Dictionary<string, bool> Events, PushEventInfo[] EventCatalog,
    int CustomerDevices, int TeamDevices);
public record PushSettingsUpdate(bool CustomersEnabled, bool TeamEnabled, Dictionary<string, bool> Events);
public record PushCampaignRequest(string Title, string Body, string? Url, string? ImageUrl, string? Audience);
public record PushCampaignDto(int Id, DateTime CreatedAt, string Kind, string Title, string Body, string Url, string? ImageUrl, string Audience,
    int Devices, int Sent, int Failed, string? CreatedBy);
public record PushAudienceDto(int All, int Ordered, int NotOrdered);

/// <summary>An app notification; Url opens when it's tapped, Tag replaces an earlier one about the same thing, Image is the big picture.</summary>
public record PushMessage(string Title, string Body, string Url, string Tag, string? Image = null);

/// <summary>
/// App notifications (Web Push). Every switchable email has an app twin, plus app-only offers, new arrivals and reminders
/// (<see cref="PushEvents"/>); each can be switched off in Admin → Email &amp; notifications → App notifications, with one switch
/// for all customer and one for all team notifications. Admin can also write a notification to customers (campaigns).
/// Team devices are turned on in Admin; customers turn them on from the installed app's prompt or their order page.
/// Sending happens in the background (<see cref="PushWorker"/>).
/// </summary>
public class PushService(AppDbContext db, IDataProtectionProvider dp, PushQueue queue, IOptions<SmtpOptions> urls)
{
    private readonly IDataProtector _protector = dp.CreateProtector("WoodsyCart.PushKeys.v1");
    public string Store => (urls.Value.StorefrontUrl ?? "https://woodsycart.com").TrimEnd('/');

    private async Task<PushConfig> ConfigAsync()
    {
        var c = await db.PushConfigs.FirstOrDefaultAsync();
        if (c is null)
        {
            var (pub, priv) = WebPushClient.NewKeys();
            db.PushConfigs.Add(c = new PushConfig { PublicKey = pub, PrivateKeyProtected = _protector.Protect(priv) });
            await db.SaveChangesAsync();
        }
        return c;
    }

    /// <summary>The server's key pair; made (and saved) the first time it's needed.</summary>
    public async Task<(string Public, string Private)> KeysAsync()
    {
        var c = await ConfigAsync();
        return (c.PublicKey, _protector.Unprotect(c.PrivateKeyProtected));
    }

    private static Dictionary<string, bool> Off(PushConfig c) =>
        string.IsNullOrEmpty(c.EventsJson) ? [] : JsonSerializer.Deserialize<Dictionary<string, bool>>(c.EventsJson) ?? [];

    // ---------- Settings (Admin → App notifications) ----------

    public async Task<PushSettingsDto> SettingsAsync()
    {
        var c = await ConfigAsync();
        var off = Off(c);
        var counts = await db.PushSubscriptions.GroupBy(s => s.IsAdmin).Select(g => new { g.Key, N = g.Count() }).ToListAsync();
        return new(c.CustomersEnabled, c.TeamEnabled, PushEvents.All.ToDictionary(e => e.Key, e => off.GetValueOrDefault(e.Key, true)), PushEvents.All,
            counts.FirstOrDefault(x => !x.Key)?.N ?? 0, counts.FirstOrDefault(x => x.Key)?.N ?? 0);
    }

    public async Task SaveSettingsAsync(PushSettingsUpdate r)
    {
        var c = await ConfigAsync();
        c.CustomersEnabled = r.CustomersEnabled; c.TeamEnabled = r.TeamEnabled;
        var off = r.Events.Where(e => PushEvents.Keys.Contains(e.Key) && !e.Value).ToDictionary(e => e.Key, _ => false);
        c.EventsJson = off.Count == 0 ? null : JsonSerializer.Serialize(off);
        c.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>The notification's own switch and its section's (customers / team) are both on.</summary>
    public async Task<bool> IsOnAsync(string key, bool team)
    {
        if (!PushEvents.Keys.Contains(key)) return false; // never password reset links or tests
        var c = await ConfigAsync();
        return (team ? c.TeamEnabled : c.CustomersEnabled) && Off(c).GetValueOrDefault(key, true);
    }

    // ---------- Sending ----------

    /// <summary>To the customer's devices: those turned on with this email, from this order, or signed in as the order's customer.</summary>
    public async Task CustomerAsync(EmailEvent ev, string? email, int? orderId, PushMessage m)
    {
        if (!await IsOnAsync(ev.ToString(), team: false)) return;
        Enqueue(new PushJob(m, await CustomerDevicesAsync(email, orderId)));
    }

    public async Task<List<int>> CustomerDevicesAsync(string? email, int? orderId)
    {
        email = RealEmail(email);
        if (email is null && orderId is null) return [];
        int? userId = orderId is null ? null : await db.Orders.Where(o => o.Id == orderId).Select(o => (int?)o.UserId).FirstOrDefaultAsync();
        return await db.PushSubscriptions.Where(s => !s.IsAdmin &&
                ((email != null && s.Email == email) || (orderId != null && s.OrderId == orderId) || (userId != null && s.UserId == userId)))
            .Select(s => s.Id).ToListAsync();
    }

    /// <summary>To every active staff and admin device.</summary>
    public async Task AdminsAsync(EmailEvent ev, PushMessage m)
    {
        if (!await IsOnAsync(ev.ToString(), team: true)) return;
        Enqueue(new PushJob(m, await db.PushSubscriptions.Where(s => s.IsAdmin && s.User!.Role != UserRole.Customer && s.User.IsActive).Select(s => s.Id).ToListAsync()));
    }

    public void Enqueue(PushJob job) { if (job.SubscriptionIds.Count > 0) queue.Enqueue(job); }

    /// <summary>Title, a short line from the email and the email's button link, as an app notification.</summary>
    public static PushMessage FromEmail(EmailEvent ev, string subject, string bodyHtml, string? url, int? orderId) =>
        new(subject, FirstLine(bodyHtml), url ?? "", orderId is null ? ev.ToString() : $"{ev}-{orderId}");

    /// <summary>The first paragraph of an email as plain text, at most 160 characters.</summary>
    public static string FirstLine(string html)
    {
        var m = Regex.Match(html, "<p[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var text = m.Success ? m.Groups[1].Value : html;
        text = Regex.Replace(text, "<br\\s*/?>", " ", RegexOptions.IgnoreCase);
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", ""));
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > 160 ? text[..157].TrimEnd() + "…" : text;
    }

    /// <summary>A store link for a notification ("/shop" or a full store link), tagged so visits from notifications show in Visitors.</summary>
    public string StoreLink(string? url, string campaign)
    {
        url = string.IsNullOrWhiteSpace(url) ? "/" : url.Trim();
        if (url.StartsWith(Store, StringComparison.OrdinalIgnoreCase)) url = url[Store.Length..];
        if (url.Length == 0) url = "/";
        if (!url.StartsWith('/') || url.StartsWith("//")) throw new AppException($"Use a page of the store (starting with / or {Store}).");
        var hash = url.IndexOf('#');
        var (path, frag) = hash < 0 ? (url, "") : (url[..hash], url[hash..]);
        return $"{Store}{path}{(path.Contains('?') ? '&' : '?')}utm_source=app&utm_medium=notification&utm_campaign={Uri.EscapeDataString(campaign)}{frag}";
    }

    // ---------- Campaigns (Admin → App notifications → Send a notification) ----------

    /// <summary>Customer devices that take offers and news, by audience: All, Ordered (bought before), NotOrdered.</summary>
    private IQueryable<PushSubscription> Audience(string audience)
    {
        var q = db.PushSubscriptions.Where(s => !s.IsAdmin && s.Promotions);
        var ordered = q.Where(s => s.OrderId != null
            || (s.Email != null && db.Orders.Any(o => o.ShipEmail == s.Email))
            || (s.UserId != null && db.Orders.Any(o => o.UserId == s.UserId)));
        return audience switch { "Ordered" => ordered, "NotOrdered" => q.Except(ordered), _ => q };
    }

    public async Task<PushAudienceDto> AudienceAsync() =>
        new(await Audience("All").CountAsync(), await Audience("Ordered").CountAsync(), await Audience("NotOrdered").CountAsync());

    /// <summary>The picture for a notification: the one given, or the product's photo / offer's banner the link points to.</summary>
    public async Task<string?> ImageForAsync(string? image, string link)
    {
        if (!string.IsNullOrWhiteSpace(image)) return AbsoluteMedia(image.Trim());
        var path = new Uri(link).AbsolutePath;
        if (Regex.Match(path, "^/product/([^/?#]+)") is { Success: true } pm)
        {
            var slug = Uri.UnescapeDataString(pm.Groups[1].Value);
            return AbsoluteMedia(await db.Products.Where(p => p.Slug == slug)
                .Select(p => p.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).Select(i => i.LocalPath ?? i.Url).FirstOrDefault())
                .FirstOrDefaultAsync());
        }
        if (Regex.Match(path, "^/offers/([^/?#]+)") is { Success: true } om)
        {
            var slug = Uri.UnescapeDataString(om.Groups[1].Value);
            return AbsoluteMedia(await db.Offers.Where(o => o.Slug == slug).Select(o => o.BannerImageUrl).FirstOrDefaultAsync());
        }
        return null;
    }

    /// <summary>Photos stored on our server ("/media/…") as a full link the phone can load.</summary>
    public string? AbsoluteMedia(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
        var api = urls.Value.ApiUrl?.TrimEnd('/');
        return string.IsNullOrEmpty(api) ? null : api + (url.StartsWith('/') ? url : "/" + url);
    }

    private static (string Title, string Body) Clean(string title, string body)
    {
        title = title.Trim(); body = body.Trim();
        if (title.Length is 0 or > 65) throw new AppException("Write a title of up to 65 characters.");
        if (body.Length is 0 or > 180) throw new AppException("Write a message of up to 180 characters.");
        return (title, body);
    }

    /// <summary>The notification to this person's own team devices first, to see how it looks.</summary>
    public async Task<int> TestCampaignAsync(int userId, PushCampaignRequest r)
    {
        var (title, body) = Clean(r.Title, r.Body);
        var url = StoreLink(r.Url, "test");
        var ids = await db.PushSubscriptions.Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync();
        if (ids.Count == 0) throw new AppException("Turn on notifications for this device first (Notifications on this device).");
        queue.Enqueue(new PushJob(new PushMessage(title, body, url, "campaign-test", await ImageForAsync(r.ImageUrl, url)), ids));
        return ids.Count;
    }

    public async Task<PushCampaignDto> SendCampaignAsync(PushCampaignRequest r, string? by)
    {
        var (title, body) = Clean(r.Title, r.Body);
        if (!(await ConfigAsync()).CustomersEnabled) throw new AppException("Customer app notifications are switched off — switch them on first.");
        var audience = r.Audience is "Ordered" or "NotOrdered" ? r.Audience : "All";
        StoreLink(r.Url, "check"); // a bad link stops it before anything is saved
        var c = new PushCampaign { Kind = "Custom", Title = title, Body = body, Audience = audience, CreatedBy = by, Url = "" };
        db.PushCampaigns.Add(c);
        await db.SaveChangesAsync();
        c.Url = StoreLink(r.Url, $"push-{c.Id}");
        c.ImageUrl = await ImageForAsync(r.ImageUrl, c.Url);
        var ids = await Audience(audience).Select(s => s.Id).ToListAsync();
        c.Devices = ids.Count;
        await db.SaveChangesAsync();
        Enqueue(new PushJob(new PushMessage(title, body, c.Url, $"push-{c.Id}", c.ImageUrl), ids, c.Id));
        return ToDto(c);
    }

    /// <summary>An automatic notification to everyone who takes offers (offer live, new arrivals), at most one a day per device.</summary>
    public async Task<PushCampaign?> BroadcastAsync(string kind, PushMessage m)
    {
        var since = DateTime.UtcNow.AddHours(-20);
        var subs = await Audience("All").Where(s => s.LastPromoAt == null || s.LastPromoAt < since).ToListAsync();
        var c = new PushCampaign { Kind = kind, Title = m.Title, Body = m.Body, Url = m.Url, ImageUrl = m.Image, Audience = "All", Devices = subs.Count, CreatedBy = "Automatic" };
        db.PushCampaigns.Add(c);
        foreach (var s in subs) s.LastPromoAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Enqueue(new PushJob(m, subs.Select(s => s.Id).ToList(), c.Id));
        return c;
    }

    public async Task<List<PushCampaignDto>> CampaignsAsync(int take = 50) =>
        (await db.PushCampaigns.AsNoTracking().OrderByDescending(c => c.Id).Take(take).ToListAsync()).Select(ToDto).ToList();

    private static PushCampaignDto ToDto(PushCampaign c) =>
        new(c.Id, c.CreatedAt, c.Kind, c.Title, c.Body, c.Url, c.ImageUrl, c.Audience, c.Devices, c.Sent, c.Failed, c.CreatedBy);

    // ---------- Team devices (Admin) ----------

    public async Task<PushStatusDto> StatusAsync(int userId, string? endpoint)
    {
        var c = await ConfigAsync();
        var mine = await db.PushSubscriptions.AsNoTracking().Where(s => s.IsAdmin && s.UserId == userId).OrderByDescending(s => s.CreatedAt).ToListAsync();
        return new(c.PublicKey, mine.Any(s => s.Endpoint == endpoint),
            mine.Select(s => new PushDeviceDto(s.Id, s.Device, s.CreatedAt, s.LastSentAt, s.LastError, s.Endpoint == endpoint)).ToList());
    }

    public async Task SubscribeAsync(int userId, PushSubscribeRequest r, string? userAgent)
    {
        var s = await UpsertAsync(r.Endpoint, r.Keys, userAgent);
        s.IsAdmin = true; s.UserId = userId; s.Email = null; s.OrderId = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Turn off one of this person's team devices: this one (by endpoint) or one from the list (by id).</summary>
    public async Task RemoveAsync(int userId, string? endpoint, int? id) =>
        await db.PushSubscriptions.Where(s => s.IsAdmin && s.UserId == userId && (s.Endpoint == endpoint || s.Id == id)).ExecuteDeleteAsync();

    /// <summary>A test notification to all of this person's team devices.</summary>
    public async Task<int> TestAsync(int userId, string adminUrl)
    {
        var ids = await db.PushSubscriptions.Where(s => s.IsAdmin && s.UserId == userId).Select(s => s.Id).ToListAsync();
        if (ids.Count == 0) throw new AppException("Turn notifications on for this device first.");
        queue.Enqueue(new PushJob(new PushMessage("WoodsyCart test 🔔", "Notifications are working on this device.", adminUrl + "/email", "test"), ids));
        return ids.Count;
    }

    // ---------- Customer devices (storefront) ----------

    public async Task<CustomerPushStatusDto> CustomerStatusAsync(string? endpoint)
    {
        var c = await ConfigAsync();
        var s = string.IsNullOrEmpty(endpoint) ? null : await db.PushSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => !s.IsAdmin && s.Endpoint == endpoint);
        return new(c.PublicKey, s is not null, c.CustomersEnabled, s?.Promotions ?? true);
    }

    /// <summary>
    /// A customer turns notifications on (or the app refreshes this device on start). From an order, the device is linked to it
    /// (guests prove it with the order link's token); signed in, to their account; the email of either gets order updates here.
    /// </summary>
    public async Task SubscribeCustomerAsync(int? userId, CustomerPushRequest r, string? userAgent)
    {
        Order? order = null;
        if (!string.IsNullOrWhiteSpace(r.OrderNumber))
        {
            order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderNumber == r.OrderNumber.Trim());
            if (order is null || (order.UserId != userId || userId is null) && order.AccessToken != r.Token) throw new AppException("Order not found.", 404);
        }
        var email = order is not null ? RealEmail(order.ShipEmail) : null;
        if (email is null && userId is not null) email = RealEmail(await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync());

        var s = await UpsertAsync(r.Endpoint, r.Keys, userAgent);
        if (s.IsAdmin) { await db.SaveChangesAsync(); return; } // a team device that also shops keeps getting team notifications
        s.UserId = userId ?? s.UserId; s.Email = email ?? s.Email; s.OrderId = order?.Id ?? s.OrderId;
        if (r.Promotions is { } p) s.Promotions = p;
        await db.SaveChangesAsync();
    }

    /// <summary>The customer's choice on this device: offers, new arrivals and reminders too, or order updates only.</summary>
    public async Task SetPreferencesAsync(PushPreferencesRequest r) =>
        await db.PushSubscriptions.Where(s => !s.IsAdmin && s.Endpoint == r.Endpoint)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Promotions, r.Promotions));

    /// <summary>The cart on this device changed: remembered for one "your cart is waiting" reminder (reset when items are added).</summary>
    public async Task CartAsync(PushCartRequest r)
    {
        var s = await db.PushSubscriptions.FirstOrDefaultAsync(s => !s.IsAdmin && s.Endpoint == r.Endpoint);
        if (s is null) return;
        var count = Math.Clamp(r.Count, 0, 999);
        if (count > s.CartCount) s.CartRemindedAt = null; // something new in the cart: worth one more reminder
        s.CartCount = count; s.CartProductId = count > 0 ? r.ProductId : null; s.CartUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task RemoveCustomerAsync(string endpoint) =>
        await db.PushSubscriptions.Where(s => !s.IsAdmin && s.Endpoint == endpoint).ExecuteDeleteAsync();

    // ---------- Helpers ----------

    private async Task<PushSubscription> UpsertAsync(string endpoint, PushKeysDto? keys, string? userAgent)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new AppException("The browser gave an invalid notification address.");
        if (string.IsNullOrWhiteSpace(keys?.P256dh) || string.IsNullOrWhiteSpace(keys.Auth)) throw new AppException("The browser didn't give its notification keys.");
        var s = await db.PushSubscriptions.FirstOrDefaultAsync(x => x.Endpoint == endpoint);
        if (s is null) db.PushSubscriptions.Add(s = new PushSubscription { Endpoint = endpoint });
        else s.UpdatedAt = DateTime.UtcNow;
        s.P256dh = keys.P256dh; s.Auth = keys.Auth; s.Device = DeviceName(userAgent); s.LastError = null;
        return s;
    }

    private static string? RealEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) || OrderService.IsPlaceholderEmail(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>"Android · Chrome", "iPhone · Safari", "Windows · Edge" … (no version numbers).</summary>
    public static string? DeviceName(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return null;
        var os = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android"
            : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "Mac" : ua.Contains("Linux") ? "Linux" : "Device";
        var browser = ua.Contains("SamsungBrowser") ? "Samsung Internet" : ua.Contains("Edg") ? "Edge" : ua.Contains("Firefox") || ua.Contains("FxiOS") ? "Firefox"
            : ua.Contains("CriOS") || ua.Contains("Chrome") ? "Chrome" : ua.Contains("Safari") ? "Safari" : "browser";
        return $"{os} · {browser}";
    }
}

/// <summary>One notification to these devices; CampaignId counts what was sent for Admin's history.</summary>
public record PushJob(PushMessage Message, List<int> SubscriptionIds, int? CampaignId = null);

public class PushQueue
{
    private readonly Channel<PushJob> _channel = Channel.CreateUnbounded<PushJob>();
    public void Enqueue(PushJob j) => _channel.Writer.TryWrite(j);
    public IAsyncEnumerable<PushJob> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Sends queued notifications; removes devices the browser says are gone and keeps the last error for Admin.</summary>
public class PushWorker(PushQueue queue, IServiceScopeFactory scopes, IOptions<SmtpOptions> urls, ILogger<PushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var client = scope.ServiceProvider.GetRequiredService<WebPushClient>();
                var (pub, priv) = await scope.ServiceProvider.GetRequiredService<PushService>().KeysAsync();
                var subject = urls.Value.AdminUrl is { } a && a.StartsWith("https://") ? a : "https://woodsycart.com";
                var payload = Payload(job.Message);
                int sent = 0, failed = 0;
                // in batches, so a big send doesn't hold every device in memory
                foreach (var chunk in job.SubscriptionIds.Chunk(200))
                {
                    foreach (var s in await db.PushSubscriptions.Where(x => chunk.Contains(x.Id)).ToListAsync(stoppingToken))
                    {
                        var r = await client.SendAsync(new PushTarget(s.Endpoint, s.P256dh, s.Auth), payload, pub, priv, subject, stoppingToken);
                        if (r.Gone) { failed++; db.PushSubscriptions.Remove(s); logger.LogInformation("Push subscription {Id} ({Device}) is gone; removed", s.Id, s.Device); }
                        else if (r.Ok) { sent++; s.LastSentAt = DateTime.UtcNow; s.LastError = null; }
                        else { failed++; s.LastError = r.Error?.Length > 500 ? r.Error[..500] : r.Error; logger.LogWarning("Push to {Device} failed: {Error}", s.Device, r.Error); }
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    db.ChangeTracker.Clear();
                }
                if (job.CampaignId is { } id)
                    await db.PushCampaigns.Where(c => c.Id == id).ExecuteUpdateAsync(u => u.SetProperty(c => c.Sent, c => c.Sent + sent).SetProperty(c => c.Failed, c => c.Failed + failed), stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Push notification failed"); }
        }
    }

    /// <summary>What the apps' service worker (Angular) shows; tapping opens the link (the order, offer, product… in Admin or on the store).</summary>
    private static object Payload(PushMessage m) => new
    {
        notification = new
        {
            title = m.Title, body = m.Body, tag = m.Tag, renotify = true, image = m.Image,
            icon = "icons/icon-192x192.png", badge = "icons/icon-96x96.png",
            data = new { url = m.Url, onActionClick = new { @default = new { operation = "navigateLastFocusedOrOpen", url = m.Url } } }
        }
    };
}
