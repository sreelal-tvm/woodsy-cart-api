using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Contracts;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Email;
using static WoodsyCart.Infrastructure.Email.EmailTemplates;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>A signed-in customer's welcome offer while it can still be used.</summary>
public record WelcomeOfferDto(string Code, string Text, DateTime EndsAt, decimal MinOrder);
public record WelcomeOfferSettings(bool Enabled, string Code, DiscountType DiscountType, decimal Value, decimal? MaxDiscount, decimal MinOrder, int ValidHours);
public record WelcomeOfferStats(int Started, int Running, int Used, decimal Discount);
public record WelcomeOfferAdminDto(WelcomeOfferSettings Settings, WelcomeOfferStats Stats);

/// <summary>
/// The welcome offer: a new account gets a discount on its first order, for a limited time after signing up (48 hours by
/// default). One shared code, checked against the signed-in account, so it can't be passed around. A reminder email and app
/// notification goes a few hours before it ends.
/// </summary>
public class WelcomeOfferService(AppDbContext db, EmailSender email)
{
    /// <summary>The reminder goes when this much time (or less) is left, but not with under half an hour to go.</summary>
    private static readonly TimeSpan RemindBefore = TimeSpan.FromHours(8), RemindAtLeast = TimeSpan.FromMinutes(30);

    public static string Describe(BusinessSettings s) => s.WelcomeDiscountType == DiscountType.Percent
        ? $"{s.WelcomeDiscountValue:0.##}% off your first order" + (s.WelcomeMaxDiscount is > 0 ? $" (up to {Rs(s.WelcomeMaxDiscount.Value)})" : "")
        : $"{Rs(s.WelcomeDiscountValue)} off your first order";

    public static bool IsOn(BusinessSettings s) => s.WelcomeOfferEnabled && s.WelcomeDiscountValue > 0 && s.WelcomeValidHours > 0;

    public static WelcomeTeaserDto? Teaser(BusinessSettings s) => IsOn(s) ? new(Describe(s), s.WelcomeValidHours, s.WelcomeMinOrder) : null;

    /// <summary>"Tue 3 Nov, 5:20 pm" in India time.</summary>
    public static string When(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Push.PushAutomation.India)
            .ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture).Replace("AM", "am").Replace("PM", "pm");

    private Task<bool> HasOrderedAsync(int userId) => db.Orders.AnyAsync(o => o.UserId == userId && o.Status != OrderStatus.Cancelled);

    /// <summary>On sign-up: starts the offer's clock when it is on and the account has no orders yet (a guest who ordered before doesn't get it).</summary>
    public async Task<WelcomeOfferDto?> StartAsync(AppUser u)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        if (!IsOn(s) || u.WelcomeOfferEndsAt is not null || await HasOrderedAsync(u.Id)) return null;
        u.WelcomeOfferEndsAt = DateTime.UtcNow.AddHours(s.WelcomeValidHours);
        await db.SaveChangesAsync();
        return new(s.WelcomeCode, Describe(s), u.WelcomeOfferEndsAt.Value, s.WelcomeMinOrder);
    }

    public async Task<WelcomeOfferDto?> ForUserAsync(int userId)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        if (!IsOn(s)) return null;
        var ends = await db.Users.Where(u => u.Id == userId && !u.IsGuest).Select(u => u.WelcomeOfferEndsAt).FirstOrDefaultAsync();
        if (ends is null || ends <= DateTime.UtcNow || await HasOrderedAsync(userId)) return null;
        return new(s.WelcomeCode, Describe(s), ends.Value, s.WelcomeMinOrder);
    }

    /// <summary>
    /// When the code is the welcome code: the discount for this cart, or why it can't be used. Not the welcome code: null
    /// (an ordinary coupon). The coupon returned isn't stored; orders record its code like any other.
    /// </summary>
    public async Task<(Coupon? coupon, decimal discount, string? message)?> TryApplyAsync(string code, decimal subTotal, int? userId)
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        if (!s.WelcomeOfferEnabled || !string.Equals(code.Trim(), s.WelcomeCode, StringComparison.OrdinalIgnoreCase)) return null;
        if (!IsOn(s)) return (null, 0, "Invalid coupon code.");
        if (userId is null) return (null, 0, "Sign in to use your welcome offer.");
        var ends = await db.Users.Where(u => u.Id == userId && !u.IsGuest).Select(u => u.WelcomeOfferEndsAt).FirstOrDefaultAsync();
        if (ends is null) return (null, 0, "The welcome offer is for new accounts.");
        if (await HasOrderedAsync(userId.Value)) return (null, 0, "The welcome offer is for your first order.");
        if (ends <= DateTime.UtcNow) return (null, 0, "Your welcome offer has ended.");
        if (subTotal < s.WelcomeMinOrder) return (null, 0, $"Add items worth {Rs(s.WelcomeMinOrder - subTotal)} more to use your welcome offer.");
        var c = new Coupon
        {
            Code = s.WelcomeCode, Description = "Welcome offer", DiscountType = s.WelcomeDiscountType, Value = s.WelcomeDiscountValue,
            MaxDiscount = s.WelcomeMaxDiscount is > 0 ? s.WelcomeMaxDiscount : null, MinOrderAmount = s.WelcomeMinOrder, EndsAt = ends
        };
        var discount = c.DiscountType == DiscountType.Percent ? subTotal * c.Value / 100m : c.Value;
        if (c.MaxDiscount is not null) discount = Math.Min(discount, c.MaxDiscount.Value);
        discount = Math.Floor(Math.Min(discount, subTotal));
        return (c, discount, $"Welcome offer applied — you save {Rs(discount)}.");
    }

    /// <summary>The block for the welcome email.</summary>
    public static string EmailBlock(WelcomeOfferDto w) =>
        Info($"<b>Your welcome offer: {E(w.Text)}</b><br/>Use code <b>{E(w.Code)}</b> at checkout before <b>{When(w.EndsAt)}</b>." +
             (w.MinOrder > 0 ? $" Orders of {Rs(w.MinOrder)} or more." : ""));

    /// <summary>One reminder, a few hours before the offer ends, to customers who haven't ordered yet. Returns how many were sent.</summary>
    public async Task<int> RemindersAsync()
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        if (!IsOn(s)) return 0;
        var now = DateTime.UtcNow;
        var due = await db.Users.Where(u => u.Role == UserRole.Customer && u.IsActive && !u.IsGuest && u.WelcomeRemindedAt == null
                && u.WelcomeOfferEndsAt > now.Add(RemindAtLeast) && u.WelcomeOfferEndsAt <= now.Add(RemindBefore)
                && !u.Orders.Any(o => o.Status != OrderStatus.Cancelled))
            .Take(50).ToListAsync();
        foreach (var u in due)
        {
            var left = u.WelcomeOfferEndsAt!.Value - now;
            var hours = (int)Math.Ceiling(left.TotalHours);
            await email.CustomerAsync(EmailEvent.WelcomeOfferEnding, u.Email, $"Your welcome offer ends in {(hours <= 1 ? "an hour" : $"{hours} hours")}",
                $"Hi {u.FullName.Split(' ')[0]}, your welcome offer ends soon",
                $"<p>{E(Describe(s))} — use code <b>{E(s.WelcomeCode)}</b> at checkout before <b>{When(u.WelcomeOfferEndsAt.Value)}</b>.</p>" +
                (s.WelcomeMinOrder > 0 ? $"<p>For orders of {Rs(s.WelcomeMinOrder)} or more.</p>" : "") +
                "<p>Free delivery and assembly across Bengaluru.</p>",
                ("Shop now", email.StorefrontUrl + "/cart"));
            u.WelcomeRemindedAt = now;
        }
        await db.SaveChangesAsync();
        return due.Count;
    }

    // ---------- Admin ----------

    public async Task<WelcomeOfferAdminDto> AdminAsync()
    {
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        var now = DateTime.UtcNow;
        var started = await db.Users.CountAsync(u => u.WelcomeOfferEndsAt != null);
        var running = await db.Users.CountAsync(u => u.WelcomeOfferEndsAt > now && !u.Orders.Any(o => o.Status != OrderStatus.Cancelled));
        var orders = db.Orders.Where(o => o.CouponCode == s.WelcomeCode && o.Status != OrderStatus.Cancelled);
        return new(new(s.WelcomeOfferEnabled, s.WelcomeCode, s.WelcomeDiscountType, s.WelcomeDiscountValue, s.WelcomeMaxDiscount, s.WelcomeMinOrder, s.WelcomeValidHours),
            new(started, running, await orders.CountAsync(), await orders.SumAsync(o => (decimal?)o.Discount) ?? 0));
    }

    public async Task SaveAsync(WelcomeOfferSettings r)
    {
        var code = (r.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 3 or > 30 || !code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) throw new AppException("The code needs 3–30 letters or digits.");
        if (await db.Coupons.AnyAsync(c => c.Code == code)) throw new AppException($"A coupon named {code} already exists — pick another code.");
        if (r.Value <= 0) throw new AppException("Enter the discount.");
        if (r.DiscountType == DiscountType.Percent && r.Value > 50) throw new AppException("A percentage discount can be at most 50%.");
        if (r.ValidHours is < 1 or > 720) throw new AppException("Valid for must be between 1 and 720 hours.");
        if (r.MinOrder < 0 || r.MaxDiscount < 0) throw new AppException("Amounts can't be negative.");
        decimal? max = r.MaxDiscount is > 0 ? r.MaxDiscount : null;
        await db.BusinessSettings.ExecuteUpdateAsync(x => x
            .SetProperty(s => s.WelcomeOfferEnabled, r.Enabled).SetProperty(s => s.WelcomeCode, code)
            .SetProperty(s => s.WelcomeDiscountType, r.DiscountType).SetProperty(s => s.WelcomeDiscountValue, r.Value)
            .SetProperty(s => s.WelcomeMaxDiscount, max)
            .SetProperty(s => s.WelcomeMinOrder, r.MinOrder).SetProperty(s => s.WelcomeValidHours, r.ValidHours));
    }
}

/// <summary>Sends the welcome offer reminders, every 15 minutes between 9 am and 9 pm (India).</summary>
public class WelcomeOfferWorker(IServiceScopeFactory scopes, ILogger<WelcomeOfferWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Push.PushAutomation.India).Hour;
            if (hour is >= 9 and < 21)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<WelcomeOfferService>().RemindersAsync();
                    if (sent > 0) logger.LogInformation("Welcome offer: reminded {Count} customers", sent);
                }
                catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "Welcome offer reminders failed"); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
