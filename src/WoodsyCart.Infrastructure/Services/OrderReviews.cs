using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Contracts;
using static WoodsyCart.Infrastructure.Email.EmailTemplates;

namespace WoodsyCart.Infrastructure.Services;

/// <summary>Ratings and reviews written from a delivered order (order page, tracking page, "Rate your purchase" email link).</summary>
public partial class OrderService
{
    public static bool CanReview(Order o) => o.Status is OrderStatus.Delivered or OrderStatus.Returned;

    /// <summary>The order's customer's reviews of the products in this order.</summary>
    private async Task<List<OrderReviewDto>> ReviewsForAsync(Order o)
    {
        var ids = o.Items.Select(i => i.ProductId).Distinct().ToList();
        return await db.Reviews.AsNoTracking().Where(r => r.UserId == o.UserId && ids.Contains(r.ProductId))
            .Select(r => new OrderReviewDto(r.ProductId, r.Rating, r.Title, r.Comment, r.IsApproved, r.UpdatedAt ?? r.CreatedAt))
            .ToListAsync();
    }

    /// <summary>
    /// Adds or edits the review of one product in a delivered order. Access: the signed-in owner, the order's access token
    /// (email / guest link), or the full delivery phone number (tracking page). Every new or edited review waits for approval.
    /// </summary>
    public async Task<List<OrderReviewDto>> ReviewItemAsync(OrderAccess access, string? phone, string orderNumber, OrderReviewRequest req)
    {
        if (req.Rating is < 1 or > 5) throw new AppException("Pick a rating from 1 to 5 stars.");
        var title = Clip(req.Title, 200);
        var comment = Clip(req.Comment, 4000);

        int? userId = access.UserId;
        var token = string.IsNullOrEmpty(access.Token) ? null : access.Token;
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        var phone10 = digits.Length >= 10 ? digits[^10..] : null;
        if (userId is null && token is null && phone10 is null)
            throw digits.Length > 0 ? new AppException("To rate this order, track it with your full 10-digit mobile number.") : new AppException("Order not found", 404);

        var o = await db.Orders.Include(x => x.Items).FirstOrDefaultAsync(x => x.OrderNumber == orderNumber &&
                    ((userId != null && x.UserId == userId) || (token != null && x.AccessToken == token) || (phone10 != null && x.ShipPhone.EndsWith(phone10))))
                ?? throw new AppException("Order not found", 404);
        if (!CanReview(o)) throw new AppException("You can rate this order once it is delivered.");
        var item = o.Items.FirstOrDefault(i => i.ProductId == req.ProductId) ?? throw new AppException("This item is not part of the order.");

        var review = await db.Reviews.FirstOrDefaultAsync(r => r.ProductId == req.ProductId && r.UserId == o.UserId);
        var wasApproved = review?.IsApproved == true;
        if (review is null)
        {
            review = new Review { ProductId = req.ProductId, UserId = o.UserId };
            db.Reviews.Add(review);
        }
        review.Rating = req.Rating;
        review.Title = title;
        review.Comment = comment;
        review.OrderId = o.Id;
        review.IsVerifiedPurchase = true;
        review.IsApproved = false;
        await db.SaveChangesAsync();
        if (wasApproved) await CatalogService.RefreshRatingAsync(db, req.ProductId); // the edited text is checked again before it shows

        var who = string.IsNullOrWhiteSpace(o.ShipName) ? "A customer" : o.ShipName;
        await email.AdminsAsync(Email.EmailEvent.AdminNewReview, $"{(wasApproved ? "Edited" : "New")} {req.Rating}★ review — {item.ProductName}", "A review is waiting for approval",
            $"<p><b>{E(who)}</b> (verified buyer, order {E(o.OrderNumber)}) rated <b>{E(item.ProductName)}</b> {new string('★', req.Rating)}{new string('☆', 5 - req.Rating)}</p>" +
            (title is null && comment is null ? "" : Info($"<b>{E(title)}</b> {E(comment)}")),
            ("Approve or hide", email.AdminUrl + "/reviews"));

        return await ReviewsForAsync(o);
    }

    private static string? Clip(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }
}
