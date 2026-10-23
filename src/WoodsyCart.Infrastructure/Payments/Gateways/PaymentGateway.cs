using System.Text.Json;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;

namespace WoodsyCart.Infrastructure.Payments.Gateways;

/// <summary>
/// How the storefront hands the customer to the gateway:
/// "redirect" (open Url), "form" (POST Fields to Url) or "cashfree" (Cashfree JS checkout with SessionId).
/// </summary>
public record GatewayLaunch(string Kind, string Url, Dictionary<string, string>? Fields = null, string? SessionId = null, string? Mode = null);

public enum GatewayState { Pending, Paid, Failed }

/// <summary>A payment as reported by the gateway itself (never by the browser).</summary>
public record GatewayResult(GatewayState State, decimal? Amount = null, string? PaymentId = null, PaymentMethod? Method = null,
    string? BankReference = null, string? Description = null, DateTime? PaidAt = null);

/// <summary>One payment attempt. MerchantOrderId is unique per attempt (order number + suffix).</summary>
public record GatewayOrderContext(Order Order, string MerchantOrderId, string ReturnUrl, string NotifyUrl);

/// <summary>
/// A hosted-checkout payment gateway. Payments are always confirmed with a server-to-server status call,
/// so the return page and webhooks only trigger a check and can't fake a payment.
/// </summary>
public interface IPaymentGateway
{
    string Code { get; }
    Task<GatewayLaunch> StartAsync(GatewayOrderContext ctx, CancellationToken ct = default);
    Task<GatewayResult> GetStatusAsync(string merchantOrderId, CancellationToken ct = default);
    Task<(bool ok, string message)> TestAsync(CancellationToken ct = default);
}

public abstract class PaymentGatewayBase(HttpClient http, PaymentConfigService config) : IPaymentGateway
{
    protected HttpClient Http => http;
    public abstract string Code { get; }
    protected string Name => PaymentConfigService.Definitions.First(d => d.Code == Code).Name;

    public abstract Task<GatewayLaunch> StartAsync(GatewayOrderContext ctx, CancellationToken ct = default);
    public abstract Task<GatewayResult> GetStatusAsync(string merchantOrderId, CancellationToken ct = default);
    public abstract Task<(bool ok, string message)> TestAsync(CancellationToken ct = default);

    /// <summary>Saved settings. New payments need the provider switched on; status checks of earlier payments don't.</summary>
    protected async Task<(bool test, Dictionary<string, string> v)> SettingsAsync(bool forNewPayment = false)
    {
        var (enabled, test, values) = await config.GetAsync(Code);
        var def = PaymentConfigService.Definitions.First(d => d.Code == Code);
        if (!PaymentConfigService.IsConfigured(def, values)) throw new AppException($"{def.Name} isn't set up yet.");
        if (forNewPayment && !enabled) throw new AppException("This payment method isn't available right now. Please choose another one.");
        return (test, values);
    }

    protected static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage res, string gateway, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new AppException($"{gateway} sent an unexpected reply ({(int)res.StatusCode}).", 502); }
    }

    protected static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    protected static decimal? Dec(string? s) =>
        decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    protected static string Money(decimal v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Last 10 digits of an Indian mobile number.</summary>
    protected static string Phone(string? p)
    {
        var d = new string((p ?? "").Where(char.IsDigit).ToArray());
        return d.Length > 10 ? d[^10..] : d;
    }

    protected static string FirstName(string? name) => (name ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Customer";
}

/// <summary>Looks up a gateway implementation by provider code.</summary>
public class GatewayRegistry(IEnumerable<IPaymentGateway> gateways)
{
    public IPaymentGateway? Find(string? code) => gateways.FirstOrDefault(g => g.Code == code);
    public IPaymentGateway Get(string code) => Find(code) ?? throw new AppException("This payment method isn't available right now.");
}
