using System.Net.Mail;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Email;
using static WoodsyCart.Infrastructure.Email.EmailTemplates;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>Signed-in customers send nothing but the option; guests send their name, email and phone.</summary>
public record StockAlertRequest(int? VariantId, string? Name, string? Email, string? Phone, string? Endpoint);
/// <summary>One open "Notify me" of this visitor for the product: the option it is for (empty = the product) and the guest's key.</summary>
public record StockAlertDto(int? VariantId, string Token);
public record AdminStockAlertDto(int Id, DateTime CreatedAt, int ProductId, string Product, string? Variant, bool AvailableNow,
    string Name, string Email, string? Phone, int? UserId, bool HasDevice, DateTime? NotifiedAt);

/// <summary>
/// "Notify me" on sold-out products: saves who asked (linked to their account when signed in, or when a guest's email or phone
/// belongs to one) and, once the product or option is available again, sends one email and one app notification.
/// </summary>
public class StockAlertService(AppDbContext db, EmailSender email, Push.PushService push)
{
    /// <summary>The visitor's open requests for a product: theirs when signed in (also those left with their email before the
    /// account existed), else those whose keys this browser kept.</summary>
    public async Task<List<StockAlertDto>> MineAsync(int productId, int? userId, IReadOnlyCollection<string> tokens)
    {
        var mail = userId is null ? null : await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync();
        return await db.StockAlerts.AsNoTracking()
            .Where(a => a.ProductId == productId && a.NotifiedAt == null
                && ((userId != null && (a.UserId == userId || a.Email == mail)) || tokens.Contains(a.Token)))
            .Select(a => new StockAlertDto(a.VariantId, a.Token)).ToListAsync();
    }

    public async Task<StockAlertDto> CreateAsync(int productId, StockAlertRequest r, int? userId)
    {
        var p = await db.Products.AsNoTracking().Where(x => x.Id == productId && x.IsActive)
            .Select(x => new { x.IsAvailable, Variant = r.VariantId == null ? null : x.Variants.Where(v => v.Id == r.VariantId && v.IsActive).Select(v => new { v.IsAvailable }).FirstOrDefault() })
            .FirstOrDefaultAsync() ?? throw new AppException("Product not found", 404);
        if (r.VariantId is not null && p.Variant is null) throw new AppException("Choose an option of this product.");
        if (p.IsAvailable && (p.Variant?.IsAvailable ?? r.VariantId is null))
            throw new AppException("Good news, this is in stock now. You can order it straight away.");

        string name, mail; string? phone;
        if (userId is not null)
        {
            var u = await db.Users.AsNoTracking().FirstAsync(x => x.Id == userId);
            (name, mail, phone) = (u.FullName, u.Email, u.Phone);
        }
        else
        {
            name = r.Name?.Trim() ?? "";
            if (name.Length is < 2 or > 150) throw new AppException("Enter your name.");
            mail = r.Email?.Trim().ToLowerInvariant() ?? "";
            if (mail.Length > 256 || !MailAddress.TryCreate(mail, out var parsed) || parsed.Address != mail || !mail.Contains('.'))
                throw new AppException("Enter a valid email address.");
            phone = Mobile.Normalise(r.Phone);
            // Someone with an account who didn't sign in: the request still belongs to that account
            userId = await db.Users.Where(u => u.Email == mail).Select(u => (int?)u.Id).FirstOrDefaultAsync()
                ?? await db.Users.Where(u => u.Phone == phone && u.Role == UserRole.Customer).OrderByDescending(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        }
        var endpoint = string.IsNullOrWhiteSpace(r.Endpoint) || r.Endpoint.Length > 1000 ? null : r.Endpoint.Trim();

        // Asking twice for the same thing keeps one request (signed in or not)
        var a = await db.StockAlerts.FirstOrDefaultAsync(x => x.ProductId == productId && x.VariantId == r.VariantId && x.NotifiedAt == null
            && ((userId != null && x.UserId == userId) || x.Email == mail));
        if (a is null)
        {
            a = new StockAlert { ProductId = productId, VariantId = r.VariantId, Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant() };
            db.StockAlerts.Add(a);
        }
        a.UserId = userId ?? a.UserId; a.Name = name; a.Email = mail; a.Phone = phone ?? a.Phone; a.Endpoint = endpoint ?? a.Endpoint;
        await db.SaveChangesAsync();
        return new StockAlertDto(a.VariantId, a.Token);
    }

    /// <summary>Cancels by the request's key (the shop knows it from <see cref="MineAsync"/>), or the customer's own one.</summary>
    public async Task CancelAsync(int productId, int? variantId, int? userId, string? token) =>
        await db.StockAlerts.Where(a => a.ProductId == productId && a.VariantId == variantId && a.NotifiedAt == null
            && ((token != null && a.Token == token) || (userId != null && a.UserId == userId))).ExecuteDeleteAsync();

    /// <summary>Requests whose product (and option) is on sale again: one email and app notification each, then marked as told.
    /// Only for the given products (just restocked); all products when empty (catching up after a restart).</summary>
    public async Task<int> SendDueAsync(IReadOnlyCollection<int>? productIds = null)
    {
        var due = await db.StockAlerts.Include(a => a.Product).Include(a => a.Variant)
            .Where(a => a.NotifiedAt == null && (productIds == null || productIds.Contains(a.ProductId)) && a.Product!.IsActive && a.Product.IsAvailable && a.Product.SellingPrice > 0
                && (a.VariantId == null || (a.Variant!.IsActive && a.Variant.IsAvailable && a.Variant.SellingPrice > 0)))
            .OrderBy(a => a.Id).Take(100).ToListAsync();
        foreach (var a in due)
        {
            var p = a.Product!;
            var price = a.Variant is { } v ? v.OfferPrice ?? v.SellingPrice : p.OfferPrice ?? p.SellingPrice;
            var what = a.Variant is null ? p.Name : $"{p.Name} ({a.Variant.Name})";
            var image = await db.ProductImages.Where(i => i.ProductId == p.Id && i.MediaType == MediaType.Image && (i.VariantId == null || i.VariantId == a.VariantId))
                .OrderBy(i => i.VariantId == a.VariantId && a.VariantId != null ? 0 : 1).ThenBy(i => i.SortOrder)
                .Select(i => i.LocalPath ?? i.Url).FirstOrDefaultAsync();
            // Their signed-in devices, the device they asked from, and devices getting order updates for this email
            var devices = await db.PushSubscriptions.Where(s => !s.IsAdmin
                    && ((a.UserId != null && s.UserId == a.UserId) || (a.Endpoint != null && s.Endpoint == a.Endpoint) || s.Email == a.Email))
                .Select(s => s.Id).ToListAsync();
            await email.CustomerAsync(EmailEvent.BackInStock, a.Email, $"Back in stock: {p.Name}", "It's back in stock!",
                $"<p>{E(what)} is available again at {Rs(price)}.</p>" +
                $"<p>Hi {E(a.Name.Split(' ')[0])}, you asked us to let you know. Popular pieces can sell out again, so order soon if you'd like it.</p>",
                ("Order now", push.StoreLink($"/product/{p.Slug}", "back-in-stock")), devices: devices, image: push.AbsoluteMedia(image));
            a.NotifiedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
        return due.Count;
    }

    // ---------- Admin ----------

    public async Task<List<AdminStockAlertDto>> ListAsync(bool notified, int? productId) =>
        await db.StockAlerts.AsNoTracking()
            .Where(a => (notified ? a.NotifiedAt != null : a.NotifiedAt == null) && (productId == null || a.ProductId == productId))
            .OrderByDescending(a => notified ? a.NotifiedAt : a.CreatedAt).Take(500)
            .Select(a => new AdminStockAlertDto(a.Id, a.CreatedAt, a.ProductId, a.Product!.Name, a.Variant != null ? a.Variant.Name : null,
                a.Product.IsActive && a.Product.IsAvailable && (a.VariantId == null || (a.Variant!.IsActive && a.Variant.IsAvailable)),
                a.Name, a.Email, a.Phone, a.UserId,
                db.PushSubscriptions.Any(s => !s.IsAdmin && ((a.UserId != null && s.UserId == a.UserId) || (a.Endpoint != null && s.Endpoint == a.Endpoint) || s.Email == a.Email)),
                a.NotifiedAt))
            .ToListAsync();

    public Task DeleteAsync(int id) => db.StockAlerts.Where(a => a.Id == id).ExecuteDeleteAsync();
}

/// <summary>Products that just came back in stock, waiting for <see cref="StockAlertWorker"/>.</summary>
public class StockAlertQueue
{
    private readonly Channel<int> channel = Channel.CreateUnbounded<int>();
    public void Restocked(int productId) => channel.Writer.TryWrite(productId);
    public ChannelReader<int> Reader => channel.Reader;
}

/// <summary>
/// Spots a product or option going from sold out to in stock, whatever saved it (Admin's product page, the vendor sync).
/// Editing a sold-out product's name or description doesn't count: only the stock switch changing does.
/// </summary>
public class StockAlertInterceptor(StockAlertQueue queue) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> result)
    {
        Collect(e.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Collect(e.Context);
        return ValueTask.FromResult(result);
    }

    private void Collect(DbContext? db)
    {
        if (db is null) return;
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Modified) continue;
            var productId = entry.Entity switch { Product p => p.Id, ProductVariant v => v.ProductId, _ => 0 };
            if (productId == 0) continue;
            var available = entry.Property(nameof(Product.IsAvailable));
            if (available.IsModified && available.OriginalValue is false && available.CurrentValue is true) queue.Restocked(productId);
        }
    }
}

/// <summary>
/// Sends "Notify me" emails and app notifications as soon as a product is restocked (a few seconds later, so a vendor sync's
/// changes go together). Restocked at night: they go at 9 am India time. After a restart it catches up once.
/// </summary>
public class StockAlertWorker(StockAlertQueue queue, IServiceScopeFactory scopes, ILogger<StockAlertWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new HashSet<int>();
        var catchUp = true;
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!catchUp)
                {
                    pending.Add(await queue.Reader.ReadAsync(stoppingToken));
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                while (queue.Reader.TryRead(out var id)) pending.Add(id);
                await Task.Delay(UntilDaytime(), stoppingToken);
                while (queue.Reader.TryRead(out var id)) pending.Add(id);

                using var scope = scopes.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<StockAlertService>().SendDueAsync(catchUp ? null : pending);
                if (sent > 0) logger.LogInformation("Back in stock: told {Count} customers", sent);
                pending.Clear();
                catchUp = false;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                logger.LogError(e, "Back in stock notifications failed");
                try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Nothing between 9 pm and 9 am India time (no phone buzzing at night).</summary>
    private static TimeSpan UntilDaytime()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Push.PushAutomation.India);
        if (now.Hour is >= 9 and < 21) return TimeSpan.Zero;
        var nine = now.Date.AddHours(now.Hour >= 21 ? 33 : 9);
        return nine - now;
    }
}
