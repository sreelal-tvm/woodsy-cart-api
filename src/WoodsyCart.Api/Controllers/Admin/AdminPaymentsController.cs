using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Payments;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Api.Controllers.Admin;

public record ParseRequest(string Text);
public record ManualAlertRequest(string Text);
public record MatchRequest(int OrderId);
public record ConfirmPaymentRequest(decimal Amount, string? Reference);
public record ProviderTestResult(bool Ok, string Message);

public record AwaitingPaymentRow(int Id, string OrderNumber, DateTime CreatedAt, string Customer, string Phone, string? PaymentProvider,
    PaymentMethod PaymentMethod, decimal Total, decimal? UpiAmount, decimal AmountPaid, string? CustomerUtr);

public record AlertRow(int Id, DateTime CreatedAt, string Source, string? Sender, string RawText, decimal? Amount, string? Utr,
    PaymentAlertStatus Status, int? OrderId, string? OrderNumber, string? Note);

/// <summary>Admin → Payment integrations: providers, checkout methods and direct-UPI verification.</summary>
[Route("api/admin/payments")]
public class AdminPaymentsController(AppDbContext db, PaymentConfigService config, PaymentAlertService alerts,
    OrderService orders, RazorpayService razorpay) : AdminControllerBase
{
    [HttpGet("providers")]
    public Task<List<ProviderView>> Providers() => config.GetProvidersAsync();

    [HttpPut("providers/{code}")]
    public async Task<List<ProviderView>> UpdateProvider(string code, ProviderUpdate update)
    {
        await config.UpdateProviderAsync(code, update);
        return await config.GetProvidersAsync();
    }

    [HttpPost("providers/{code}/test")]
    public async Task<ProviderTestResult> Test(string code)
    {
        var (_, _, v) = await config.GetAsync(code);
        switch (code)
        {
            case PaymentProviders.Razorpay:
                if (string.IsNullOrEmpty(v.GetValueOrDefault("keyId")) || string.IsNullOrEmpty(v.GetValueOrDefault("keySecret")))
                    return new(false, "Save the key ID and key secret first.");
                var (ok, msg) = await razorpay.TestAsync(v["keyId"], v["keySecret"]);
                return new(ok, msg);
            case PaymentProviders.DirectUpi:
                var vpa = v.GetValueOrDefault("upiId");
                return string.IsNullOrEmpty(vpa)
                    ? new(false, "Add your business UPI ID first.")
                    : new(true, $"UPI ID {vpa} looks valid. Make a ₹1 test payment from the storefront to confirm alerts reach the app.");
            case PaymentProviders.BankTransfer:
                return new(!string.IsNullOrEmpty(v.GetValueOrDefault("accountNumber")), "Bank details are shown to customers exactly as saved.");
            default:
                return new(false, "This provider's checkout integration isn't built yet — your credentials are saved for later.");
        }
    }

    [HttpGet("methods")]
    public Task<List<MethodOptionDto>> Methods() => config.GetMethodsAsync();

    [HttpPut("methods")]
    public async Task<List<MethodOptionDto>> UpdateMethods(List<MethodOptionUpdate> updates)
    {
        await config.UpdateMethodsAsync(updates);
        return await config.GetMethodsAsync();
    }

    /// <summary>Webhook address and key for the SMS/notification forwarder app.</summary>
    [HttpGet("upi/setup")]
    public async Task<object> UpiSetup()
    {
        var (_, _, v) = await config.GetAsync(PaymentProviders.DirectUpi);
        var key = v.GetValueOrDefault("alertKey");
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return new { webhookUrl = $"{baseUrl}/api/payments/upi/alert?key={key}", key, hasKey = !string.IsNullOrEmpty(key) };
    }

    [HttpPost("upi/regenerate-key")]
    public async Task<object> RegenerateKey()
    {
        await config.RegenerateAlertKeyAsync();
        return await UpiSetup();
    }

    /// <summary>Preview how a bank SMS would be read (nothing is saved).</summary>
    [HttpPost("upi/parse")]
    public ParsedAlert Parse(ParseRequest req) => PaymentAlertService.Parse(req.Text ?? "");

    [HttpGet("alerts")]
    public async Task<List<AlertRow>> Alerts(PaymentAlertStatus? status, int take = 100) =>
        await db.PaymentAlerts.AsNoTracking().Where(a => status == null || a.Status == status)
            .OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500))
            .Select(a => new AlertRow(a.Id, a.CreatedAt, a.Source, a.Sender, a.RawText, a.Amount, a.Utr, a.Status, a.OrderId,
                a.Order != null ? a.Order.OrderNumber : null, a.Note))
            .ToListAsync();

    /// <summary>Paste a bank SMS by hand (e.g. if the forwarder was offline).</summary>
    [HttpPost("alerts")]
    public Task<AlertResult> AddAlert(ManualAlertRequest req) => alerts.ReceiveAsync(req.Text ?? "", "manual", UserName);

    [HttpPost("alerts/{id:int}/match")]
    public Task<AlertResult> Match(int id, MatchRequest req) => alerts.MatchManuallyAsync(id, req.OrderId, UserName);

    [HttpPost("alerts/{id:int}/ignore")]
    public async Task<IActionResult> Ignore(int id)
    {
        await db.PaymentAlerts.Where(a => a.Id == id && a.Status != PaymentAlertStatus.Matched)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, PaymentAlertStatus.Ignored));
        return NoContent();
    }

    /// <summary>Unpaid orders waiting for a direct UPI / bank transfer payment.</summary>
    [HttpGet("awaiting")]
    public Task<List<AwaitingPaymentRow>> Awaiting() =>
        db.Orders.AsNoTracking()
            .Where(o => o.PaymentStatus != PaymentStatus.Paid && o.Status != OrderStatus.Cancelled
                        && (o.PaymentProvider == PaymentProviders.DirectUpi || o.PaymentProvider == PaymentProviders.BankTransfer))
            .OrderByDescending(o => o.CustomerUtr != null).ThenByDescending(o => o.CreatedAt)
            .Select(o => new AwaitingPaymentRow(o.Id, o.OrderNumber, o.CreatedAt, o.ShipName, o.ShipPhone, o.PaymentProvider, o.PaymentMethod,
                o.Total, o.UpiAmount, o.AmountPaid, o.CustomerUtr))
            .ToListAsync();

    /// <summary>Admin checked the bank/UPI app and confirms the payment.</summary>
    [HttpPost("orders/{orderId:int}/confirm")]
    public async Task<IActionResult> Confirm(int orderId, ConfirmPaymentRequest req)
    {
        var order = await db.Orders.FindAsync(orderId) ?? throw new AppException("Order not found", 404);
        if (req.Amount <= 0) throw new AppException("Enter the amount received.");
        await orders.ConfirmPaymentAsync(order, req.Amount, string.IsNullOrWhiteSpace(req.Reference) ? order.CustomerUtr : req.Reference.Trim(),
            "verified by admin in bank/UPI app", UserName);
        return NoContent();
    }
}
