using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Data;
using static WoodsyCart.Infrastructure.Email.EmailTemplates;

namespace WoodsyCart.Infrastructure.Push;

/// <summary>
/// The automatic customer app notifications, checked every 10 minutes between 9 am and 9 pm (India): payment pending, rate
/// your purchase, offer is live, new arrivals, cart waiting and wishlist price drop / back in stock. Each goes once (marked on
/// the order, offer, product, device or wishlist item), and a device gets at most one offer / news / reminder a day.
/// </summary>
public class PushAutomationWorker(IServiceScopeFactory scopes, ILogger<PushAutomationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            var hour = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, PushAutomation.India).Hour;
            if (hour is >= 9 and < 21)
            {
                using var scope = scopes.CreateScope();
                var run = new PushAutomation(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<PushService>());
                foreach (var (name, step) in new (string, Func<Task>)[]
                {
                    ("payment reminders", run.PaymentRemindersAsync), ("review reminders", run.ReviewRemindersAsync),
                    ("offers", run.OffersAsync), ("wishlist", run.WishlistAsync), ("carts", run.CartsAsync),
                    ("new arrivals", () => hour is >= 11 and < 20 ? run.NewArrivalsAsync() : Task.CompletedTask)
                })
                {
                    try { await step(); }
                    catch (Exception e) when (e is not OperationCanceledException) { logger.LogError(e, "App notifications: {Step} failed", name); }
                }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}

/// <param name="now">The time to work from (tests); otherwise now.</param>
public class PushAutomation(AppDbContext db, PushService push, DateTime? now = null)
{
    public static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "India Standard Time" : "Asia/Kolkata");
    private DateTime Now { get; } = now ?? DateTime.UtcNow;
    /// <summary>Devices that haven't had an offer / news / reminder in the last 20 hours.</summary>
    private DateTime PromoSince => Now.AddHours(-20);

    /// <summary>An online order still unpaid an hour after it was placed (up to 2 days): one reminder with the link to pay.</summary>
    public async Task PaymentRemindersAsync()
    {
        var orders = await db.Orders.Where(o => o.Status == OrderStatus.Pending && o.PaymentStatus == PaymentStatus.Pending
                && o.PaymentMethod != PaymentMethod.CashOnDelivery && o.PaymentReminderAt == null
                && o.CreatedAt < Now.AddHours(-1) && o.CreatedAt > Now.AddDays(-2))
            .Take(50).ToListAsync();
        if (orders.Count == 0) return;
        var on = await push.IsOnAsync(PushEvents.PaymentReminder, team: false);
        foreach (var o in orders)
        {
            o.PaymentReminderAt = Now;
            if (!on) continue;
            var url = push.StoreLink($"/order/{o.OrderNumber}?token={o.AccessToken}", "payment-reminder");
            push.Enqueue(new PushJob(new PushMessage("Complete your payment",
                $"Order {o.OrderNumber} ({Rs(o.Total - o.AmountPaid)}) is reserved for you. Pay now so we can start making it.", url, $"payment-{o.Id}"),
                await push.CustomerDevicesAsync(o.ShipEmail, o.Id)));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>3 days after delivery (up to 2 weeks), if the order has no rating yet.</summary>
    public async Task ReviewRemindersAsync()
    {
        var orders = await db.Orders.Where(o => o.Status == OrderStatus.Delivered && o.ReviewReminderAt == null
                && o.DeliveredAt < Now.AddDays(-3) && o.DeliveredAt > Now.AddDays(-14) && !db.Reviews.Any(r => r.OrderId == o.Id))
            .Select(o => new { Order = o, Item = o.Items.OrderBy(i => i.Id).Select(i => new { i.ProductName, i.ImageUrl }).FirstOrDefault() })
            .Take(50).ToListAsync();
        if (orders.Count == 0) return;
        var on = await push.IsOnAsync(PushEvents.ReviewReminder, team: false);
        foreach (var x in orders)
        {
            var o = x.Order;
            o.ReviewReminderAt = Now;
            if (!on) continue;
            var url = push.StoreLink($"/order/{o.OrderNumber}?token={o.AccessToken}", "review-reminder");
            push.Enqueue(new PushJob(new PushMessage($"How's your new {x.Item?.ProductName ?? "furniture"}?",
                "Rate it in a tap — it helps other families choose.", url, $"review-{o.Id}", push.AbsoluteMedia(x.Item?.ImageUrl)),
                await push.CustomerDevicesAsync(o.ShipEmail, o.Id)));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>An offer that started in the last 18 hours: one notification to everyone, opening the offer page.</summary>
    public async Task OffersAsync()
    {
        var offer = await db.Offers.Where(o => o.IsActive && o.PushSentAt == null && o.StartsAt <= Now && o.EndsAt > Now && o.StartsAt > Now.AddHours(-18))
            .OrderBy(o => o.StartsAt).FirstOrDefaultAsync();
        if (offer is null) return;
        offer.PushSentAt = Now;
        await db.SaveChangesAsync();
        if (!await push.IsOnAsync(PushEvents.OfferLive, team: false)) return;
        var ends = TimeZoneInfo.ConvertTimeFromUtc(offer.EndsAt, India);
        var url = push.StoreLink($"/offers/{offer.Slug}", $"offer-{offer.Id}");
        await push.BroadcastAsync("OfferLive", new PushMessage($"{offer.Name} is live 🎉",
            $"{(string.IsNullOrWhiteSpace(offer.Tagline) ? "Special prices for a limited time." : offer.Tagline.Trim().TrimEnd('.') + ".")} Ends {ends:d MMM}.",
            url, $"offer-{offer.Id}", push.AbsoluteMedia(offer.BannerImageUrl)));
    }

    /// <summary>Newly published products, at most once a day: one product opens its page, more open the newest products.</summary>
    public async Task NewArrivalsAsync()
    {
        var config = await db.PushConfigs.FirstOrDefaultAsync();
        if (config is null || config.LastNewArrivalsAt > Now.AddHours(-20)) return;
        var products = await db.Products.Where(p => p.IsActive && !p.IsDeleted && p.IsAvailable && p.PushAnnouncedAt == null)
            .OrderByDescending(p => p.Id).ToListAsync();
        if (products.Count == 0) return;
        foreach (var p in products) p.PushAnnouncedAt = Now;
        config.LastNewArrivalsAt = Now;
        await db.SaveChangesAsync();
        if (!await push.IsOnAsync(PushEvents.NewArrivals, team: false)) return;

        var first = products[0];
        var image = await db.ProductImages.Where(i => i.ProductId == first.Id && i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder)
            .Select(i => i.LocalPath ?? i.Url).FirstOrDefaultAsync();
        var one = products.Count == 1;
        var url = push.StoreLink(one ? $"/product/{first.Slug}" : "/shop?sort=newest", "new-arrivals");
        await push.BroadcastAsync("NewArrivals", new PushMessage(
            one ? $"Just in: {first.Name}" : $"New arrivals: {products.Count} fresh pieces",
            one ? $"Now {Rs(first.OfferPrice ?? first.SellingPrice)}, factory-direct. Tap to have a look."
                : $"{first.Name} and {products.Count - 1} more just landed. Tap to have a look.",
            url, "new-arrivals", push.AbsoluteMedia(image)));
    }

    /// <summary>Something left in the cart for 3 hours (up to a week): one reminder per device, opening the cart.</summary>
    public async Task CartsAsync()
    {
        if (!await push.IsOnAsync(PushEvents.CartReminder, team: false)) return;
        var subs = await db.PushSubscriptions.Where(s => !s.IsAdmin && s.Promotions && s.CartCount > 0 && s.CartRemindedAt == null
                && s.CartUpdatedAt < Now.AddHours(-3) && s.CartUpdatedAt > Now.AddDays(-7) && (s.LastPromoAt == null || s.LastPromoAt < PromoSince))
            .Take(200).ToListAsync();
        foreach (var s in subs)
        {
            var p = s.CartProductId is null ? null : await db.Products.Where(p => p.Id == s.CartProductId && !p.IsDeleted)
                .Select(p => new { p.Name, Image = p.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).Select(i => i.LocalPath ?? i.Url).FirstOrDefault() })
                .FirstOrDefaultAsync();
            s.CartRemindedAt = Now; s.LastPromoAt = Now;
            var what = p is null ? "Your picks are" : s.CartCount > 1 ? $"{p.Name} and {s.CartCount - 1} more are" : $"{p.Name} is";
            push.Enqueue(new PushJob(new PushMessage("Your cart is waiting 🛒", $"{what} still in your cart. Finish your order in a minute.",
                push.StoreLink("/cart", "cart-reminder"), "cart", push.AbsoluteMedia(p?.Image)), [s.Id]));
        }
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Wishlist products that got at least 3% cheaper, or are available again, since last checked: one notification per customer
    /// (to their signed-in devices). The price and stock seen are updated every run, so each change is told once.
    /// </summary>
    public async Task WishlistAsync()
    {
        var items = await db.WishlistItems.Include(w => w.Product).Where(w => !w.Product!.IsDeleted && w.Product.IsActive).ToListAsync();
        if (items.Count == 0) return;
        var dropOn = await push.IsOnAsync(PushEvents.WishlistPriceDrop, team: false);
        var backOn = await push.IsOnAsync(PushEvents.WishlistBackInStock, team: false);
        var news = new Dictionary<int, PushMessage>();
        foreach (var w in items)
        {
            var p = w.Product!;
            var price = p.OfferPrice ?? p.SellingPrice;
            if (w.SeenPrice is { } seen && !news.ContainsKey(w.UserId))
            {
                if (backOn && !w.SeenAvailable && p.IsAvailable)
                    news[w.UserId] = new($"Back in stock: {p.Name}", $"It's on your wishlist — now {Rs(price)}. Grab it before it's gone again.",
                        push.StoreLink($"/product/{p.Slug}", "wishlist-back"), $"wishlist-{p.Id}");
                else if (dropOn && p.IsAvailable && price <= seen * 0.97m)
                    news[w.UserId] = new($"Price drop: {p.Name}", $"Now {Rs(price)} (was {Rs(seen)}) — it's on your wishlist.",
                        push.StoreLink($"/product/{p.Slug}", "wishlist-price"), $"wishlist-{p.Id}");
            }
            w.SeenPrice = price; w.SeenAvailable = p.IsAvailable;
        }
        if (news.Count > 0)
        {
            var userIds = news.Keys.ToList();
            var subs = await db.PushSubscriptions.Where(s => !s.IsAdmin && s.Promotions && s.UserId != null && userIds.Contains(s.UserId.Value)
                && (s.LastPromoAt == null || s.LastPromoAt < PromoSince)).ToListAsync();
            foreach (var g in subs.GroupBy(s => s.UserId!.Value))
            {
                var m = news[g.Key];
                var slug = new Uri(m.Url).AbsolutePath.Replace("/product/", "");
                var image = await db.Products.Where(p => p.Slug == slug)
                    .Select(p => p.Images.Where(i => i.MediaType == MediaType.Image).OrderBy(i => i.SortOrder).Select(i => i.LocalPath ?? i.Url).FirstOrDefault()).FirstOrDefaultAsync();
                foreach (var s in g) s.LastPromoAt = Now;
                push.Enqueue(new PushJob(m with { Image = push.AbsoluteMedia(image) }, g.Select(s => s.Id).ToList()));
            }
        }
        await db.SaveChangesAsync();
    }
}
