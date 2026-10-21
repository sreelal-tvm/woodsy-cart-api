using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Payments;

public record ParsedAlert(bool IsCredit, decimal? Amount, string? Utr, string? Reason);

public record AlertResult(int AlertId, PaymentAlertStatus Status, decimal? Amount, string? Utr, string? OrderNumber, string Message);

/// <summary>
/// Confirms direct-UPI and bank-transfer payments without a gateway: the owner's phone forwards every
/// "money received" SMS / app notification here; we read the amount and UPI reference and match the order.
/// </summary>
public partial class PaymentAlertService(AppDbContext db, OrderService orders, EmailSender email)
{
    [GeneratedRegex(@"\b(credited|received|deposited|has sent you|paid you|money received|payment received|credit(?:ed)? (?:to|in|of))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreditWords();

    [GeneratedRegex(@"\b(debited|spent|withdrawn|sent to|paid to|deducted)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DebitWords();

    [GeneratedRegex(@"(?:rs\.?|inr|₹)\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"(?:upi\s*(?:ref(?:erence)?|txn|transaction)?\s*(?:no\.?|number|id)?|utr|rrn|ref(?:erence)?\s*(?:no\.?|number|id)?|txn\s*id)\D{0,12}(\d{12})", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledUtr();

    [GeneratedRegex(@"(?<!\d)(\d{12})(?!\d)")]
    private static partial Regex AnyTwelveDigits();

    [GeneratedRegex(@"\b([A-Z]{4}[A-Z0-9]{12,18})\b")]
    private static partial Regex NeftUtr();

    public static ParsedAlert Parse(string text)
    {
        var credit = CreditWords().IsMatch(text);
        var debit = DebitWords().IsMatch(text);
        if (!credit || (debit && !Regex.IsMatch(text, @"\bcredited\b", RegexOptions.IgnoreCase)))
            return new ParsedAlert(false, null, null, "Not a money-received message.");

        decimal? amount = null;
        var m = AmountPattern().Match(text);
        if (m.Success && decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var a))
            amount = a;

        var utr = LabelledUtr().Match(text) is { Success: true } lu ? lu.Groups[1].Value
                : AnyTwelveDigits().Match(text) is { Success: true } any ? any.Groups[1].Value
                : NeftUtr().Match(text) is { Success: true } neft ? neft.Groups[1].Value : null;
        return new ParsedAlert(true, amount, utr, amount is null ? "Amount not found." : null);
    }

    /// <summary>Accepts plain text or the JSON sent by SMS-forwarder apps (all string fields are combined).</summary>
    public static (string text, string? sender) ExtractText(string body)
    {
        body = body.Trim();
        if (!body.StartsWith('{') && !body.StartsWith('[')) return (body, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var parts = new List<string>();
            string? sender = null;
            void Walk(JsonElement e, string? name)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject()) Walk(p.Value, p.Name);
                        break;
                    case JsonValueKind.Array:
                        foreach (var i in e.EnumerateArray()) Walk(i, name);
                        break;
                    case JsonValueKind.String:
                        var v = e.GetString() ?? "";
                        var n = name?.ToLowerInvariant() ?? "";
                        if (n is "from" or "sender" or "contact" or "address" or "phone" or "title" or "app" or "package") sender ??= v;
                        if (n is "content" or "message" or "msg" or "text" or "body" or "sms" or "notification") parts.Insert(0, v);
                        else if (v.Length > 25) parts.Add(v);
                        break;
                }
            }
            Walk(doc.RootElement, null);
            return (string.Join(" | ", parts.Distinct()), sender);
        }
        catch (JsonException) { return (body, null); }
    }

    public async Task<AlertResult> ReceiveAsync(string text, string source, string? sender)
    {
        if (text.Length > 2000) text = text[..2000];
        var parsed = Parse(text);
        var alert = new PaymentAlert { Source = source, RawText = text, Sender = sender, Amount = parsed.Amount, Utr = parsed.Utr };

        if (!parsed.IsCredit)
        {
            alert.Status = PaymentAlertStatus.NotACredit;
            alert.Note = parsed.Reason;
        }
        else if (parsed.Utr is not null && await db.PaymentAlerts.AnyAsync(a => a.Utr == parsed.Utr && a.Status != PaymentAlertStatus.NotACredit))
        {
            alert.Status = PaymentAlertStatus.Duplicate;
            alert.Note = "Same UPI reference already received (SMS and app notification for one payment).";
        }
        db.PaymentAlerts.Add(alert);
        await db.SaveChangesAsync();

        if (alert.Status == PaymentAlertStatus.Unmatched && parsed.Amount is not null)
            return await TryMatchAsync(alert);
        return Result(alert, null, alert.Note ?? "Saved.");
    }

    private async Task<AlertResult> TryMatchAsync(PaymentAlert alert)
    {
        var since = DateTime.UtcNow.AddDays(-14);
        var open = db.Orders.Where(o => o.PaymentStatus != PaymentStatus.Paid && o.Status != OrderStatus.Cancelled && o.CreatedAt > since
                                        && (o.PaymentProvider == PaymentProviders.DirectUpi || o.PaymentProvider == PaymentProviders.BankTransfer));

        // 1) The customer already told us this reference.
        Order? order = alert.Utr is null ? null : await open.FirstOrDefaultAsync(o => o.CustomerUtr == alert.Utr);
        var how = "matched by UPI reference";

        // 2) The unique "fingerprint" amount (direct UPI) or the exact balance (bank transfer).
        if (order is null)
        {
            var amount = alert.Amount!.Value;
            var candidates = await open.Where(o => o.UpiAmount == amount - o.AmountPaid || (o.UpiAmount == null && o.Total - o.AmountPaid == amount)).ToListAsync();
            if (candidates.Count == 1) { order = candidates[0]; how = "matched by unique amount"; }
            else if (candidates.Count > 1)
            {
                alert.Note = $"{candidates.Count} unpaid orders have this amount — confirm manually.";
                await db.SaveChangesAsync();
                await NotifyUnmatchedAsync(alert);
                return Result(alert, null, alert.Note);
            }
        }

        if (order is null)
        {
            alert.Note = "No unpaid order with this amount or reference — check it in Payment integrations → UPI verification.";
            await db.SaveChangesAsync();
            await NotifyUnmatchedAsync(alert);
            return Result(alert, null, alert.Note);
        }

        alert.Status = PaymentAlertStatus.Matched;
        alert.OrderId = order.Id;
        alert.Note = how;
        await orders.ConfirmPaymentAsync(order, alert.Amount!.Value, alert.Utr, $"auto-confirmed from {alert.Source} alert ({how})", "Payment alert");
        return Result(alert, order.OrderNumber, $"Order {order.OrderNumber} marked paid ({how}).");
    }

    /// <summary>Admin links an unmatched alert to an order.</summary>
    public async Task<AlertResult> MatchManuallyAsync(int alertId, int orderId, string by)
    {
        var alert = await db.PaymentAlerts.FindAsync(alertId) ?? throw new Common.AppException("Alert not found", 404);
        if (alert.Status == PaymentAlertStatus.Matched) throw new Common.AppException("This alert is already matched.");
        if (alert.Amount is null) throw new Common.AppException("This alert has no amount — record the payment on the order instead.");
        var order = await db.Orders.FindAsync(orderId) ?? throw new Common.AppException("Order not found", 404);
        alert.Status = PaymentAlertStatus.Matched;
        alert.OrderId = order.Id;
        alert.Note = $"matched manually by {by}";
        await orders.ConfirmPaymentAsync(order, alert.Amount.Value, alert.Utr, "confirmed manually from payment alert", by);
        return Result(alert, order.OrderNumber, $"Order {order.OrderNumber} marked paid.");
    }

    private Task NotifyUnmatchedAsync(PaymentAlert a) =>
        email.AdminsAsync(Email.EmailEvent.AdminUnmatchedPayment, $"₹{a.Amount:N2} received — match it to an order", "Money received, but no order matched",
            $"<p>A payment of <b>₹{a.Amount:N2}</b>{(a.Utr is null ? "" : $" (UPI ref <b>{a.Utr}</b>)")} arrived and couldn't be matched automatically.</p>" +
            Email.EmailTemplates.Info(Email.EmailTemplates.E(a.Note)) + "<p>Match it to the right order in Payment integrations → UPI verification.</p>",
            ("Match the payment", email.AdminUrl + "/payments"));

    private static AlertResult Result(PaymentAlert a, string? orderNumber, string message) =>
        new(a.Id, a.Status, a.Amount, a.Utr, orderNumber, message);
}
