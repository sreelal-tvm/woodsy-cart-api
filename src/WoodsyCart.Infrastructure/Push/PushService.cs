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
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Push;

public record PushKeysDto(string P256dh, string Auth);
public record PushSubscribeRequest(string Endpoint, PushKeysDto Keys);
public record PushDeviceDto(int Id, string? Device, DateTime CreatedAt, DateTime? LastSentAt, string? LastError, bool ThisDevice);
public record PushStatusDto(string PublicKey, bool ThisDevice, List<PushDeviceDto> Devices);

/// <summary>A notification for the Admin team; Url opens when it's tapped.</summary>
public record AdminPush(string Title, string Body, string Url, string Tag);

/// <summary>
/// Admin notifications on phones and computers (Web Push): each device turns them on in Admin → Email &amp; notifications,
/// then new orders reach every staff and admin device. Sending happens in the background (<see cref="PushWorker"/>).
/// </summary>
public class PushService(AppDbContext db, IDataProtectionProvider dp, PushQueue queue)
{
    private readonly IDataProtector _protector = dp.CreateProtector("WoodsyCart.PushKeys.v1");

    /// <summary>The server's public key for browsers; made (and saved) the first time it's needed.</summary>
    public async Task<(string Public, string Private)> KeysAsync()
    {
        var c = await db.PushConfigs.FirstOrDefaultAsync();
        if (c is null)
        {
            var (pub, priv) = WebPushClient.NewKeys();
            c = new PushConfig { PublicKey = pub, PrivateKeyProtected = _protector.Protect(priv) };
            db.PushConfigs.Add(c);
            await db.SaveChangesAsync();
        }
        return (c.PublicKey, _protector.Unprotect(c.PrivateKeyProtected));
    }

    public async Task<PushStatusDto> StatusAsync(int userId, string? endpoint)
    {
        var (pub, _) = await KeysAsync();
        var mine = await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == userId).OrderByDescending(s => s.CreatedAt).ToListAsync();
        return new(pub, mine.Any(s => s.Endpoint == endpoint),
            mine.Select(s => new PushDeviceDto(s.Id, s.Device, s.CreatedAt, s.LastSentAt, s.LastError, s.Endpoint == endpoint)).ToList());
    }

    public async Task SubscribeAsync(int userId, PushSubscribeRequest r, string? userAgent)
    {
        if (!Uri.TryCreate(r.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new AppException("The browser gave an invalid notification address.");
        if (string.IsNullOrWhiteSpace(r.Keys?.P256dh) || string.IsNullOrWhiteSpace(r.Keys.Auth)) throw new AppException("The browser didn't give its notification keys.");
        var s = await db.PushSubscriptions.FirstOrDefaultAsync(x => x.Endpoint == r.Endpoint);
        if (s is null) db.PushSubscriptions.Add(s = new PushSubscription { Endpoint = r.Endpoint });
        else s.UpdatedAt = DateTime.UtcNow;
        s.UserId = userId; s.P256dh = r.Keys.P256dh; s.Auth = r.Keys.Auth; s.Device = DeviceName(userAgent); s.LastError = null;
        await db.SaveChangesAsync();
    }

    /// <summary>Turn off one device: this one (by endpoint) or one from the list (by id).</summary>
    public async Task RemoveAsync(int userId, string? endpoint, int? id)
    {
        await db.PushSubscriptions.Where(s => s.UserId == userId && (s.Endpoint == endpoint || s.Id == id)).ExecuteDeleteAsync();
    }

    /// <summary>A test notification to all of this person's devices.</summary>
    public async Task<int> TestAsync(int userId, string adminUrl)
    {
        var ids = await db.PushSubscriptions.Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync();
        if (ids.Count == 0) throw new AppException("Turn notifications on for this device first.");
        queue.Enqueue(new PushJob(new AdminPush("WoodsyCart test 🔔", "Notifications are working on this device.", adminUrl + "/email", "test"), ids));
        return ids.Count;
    }

    /// <summary>Notify every staff and admin device (sent in the background).</summary>
    public async Task AdminsAsync(AdminPush message)
    {
        var ids = await db.PushSubscriptions.Where(s => s.User!.Role != UserRole.Customer && s.User.IsActive).Select(s => s.Id).ToListAsync();
        if (ids.Count > 0) queue.Enqueue(new PushJob(message, ids));
    }

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

public record PushJob(AdminPush Message, List<int> SubscriptionIds);

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
                foreach (var s in await db.PushSubscriptions.Where(x => job.SubscriptionIds.Contains(x.Id)).ToListAsync(stoppingToken))
                {
                    var r = await client.SendAsync(new PushTarget(s.Endpoint, s.P256dh, s.Auth), payload, pub, priv, subject, stoppingToken);
                    if (r.Gone) { db.PushSubscriptions.Remove(s); logger.LogInformation("Push subscription {Id} ({Device}) is gone; removed", s.Id, s.Device); }
                    else if (r.Ok) { s.LastSentAt = DateTime.UtcNow; s.LastError = null; }
                    else { s.LastError = r.Error?.Length > 500 ? r.Error[..500] : r.Error; logger.LogWarning("Push to {Device} failed: {Error}", s.Device, r.Error); }
                }
                await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Push notification failed"); }
        }
    }

    /// <summary>What the Admin app's service worker (Angular) shows; tapping opens the order in Admin.</summary>
    private static object Payload(AdminPush m) => new
    {
        notification = new
        {
            title = m.Title, body = m.Body, tag = m.Tag, renotify = true,
            icon = "icons/icon-192x192.png", badge = "icons/icon-96x96.png",
            data = new { url = m.Url, onActionClick = new { @default = new { operation = "navigateLastFocusedOrOpen", url = m.Url } } }
        }
    };
}
