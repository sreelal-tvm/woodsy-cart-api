using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Payments;

namespace WoodsyCart.Infrastructure.Services;

public class RazorpayOptions
{
    public string? KeyId { get; set; }
    public string? KeySecret { get; set; }
    public string? WebhookSecret { get; set; }
    public bool Enabled => !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(KeySecret);
}

/// <summary>
/// Minimal Razorpay Orders API client (create order + signature verification).
/// Keys come from Admin → Payment integrations; appsettings "Razorpay" is used only when nothing is saved there.
/// </summary>
public class RazorpayService(HttpClient http, PaymentConfigService config, IOptions<RazorpayOptions> fallback)
{
    private RazorpayOptions? _options;

    public async Task<RazorpayOptions> GetOptionsAsync()
    {
        if (_options is not null) return _options;
        var (enabled, _, values) = await config.GetAsync(PaymentProviders.Razorpay);
        _options = values.GetValueOrDefault("keyId") is { Length: > 0 }
            ? new RazorpayOptions
            {
                KeyId = enabled ? values.GetValueOrDefault("keyId") : null,
                KeySecret = enabled ? values.GetValueOrDefault("keySecret") : null,
                WebhookSecret = values.GetValueOrDefault("webhookSecret")
            }
            : fallback.Value;
        return _options;
    }

    public async Task<string> CreateOrderAsync(long amountInPaise, string receipt, CancellationToken ct = default)
    {
        var o = await GetOptionsAsync();
        if (!o.Enabled) throw new AppException("Online payment is not configured.");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/orders")
        {
            Content = JsonContent.Create(new { amount = amountInPaise, currency = "INR", receipt, payment_capture = 1 })
        };
        req.Headers.Authorization = Basic(o);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw new AppException($"Payment gateway error: {await res.Content.ReadAsStringAsync(ct)}", 502);
        var body = await res.Content.ReadFromJsonAsync<RzpOrder>(ct);
        return body!.Id;
    }

    /// <summary>Checks the keys by listing one order.</summary>
    public async Task<(bool ok, string message)> TestAsync(string keyId, string keySecret)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.razorpay.com/v1/orders?count=1");
        req.Headers.Authorization = Basic(new RazorpayOptions { KeyId = keyId, KeySecret = keySecret });
        using var res = await http.SendAsync(req);
        return res.IsSuccessStatusCode
            ? (true, keyId.StartsWith("rzp_test") ? "Connected (test mode keys)." : "Connected (live keys).")
            : (false, $"Razorpay rejected the keys ({(int)res.StatusCode}).");
    }

    private static AuthenticationHeaderValue Basic(RazorpayOptions o) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{o.KeyId}:{o.KeySecret}")));

    public async Task<bool> VerifySignatureAsync(string orderId, string paymentId, string signature) =>
        Verify($"{orderId}|{paymentId}", signature, (await GetOptionsAsync()).KeySecret);

    public async Task<bool> VerifyWebhookAsync(string body, string signature) =>
        Verify(body, signature, (await GetOptionsAsync()).WebhookSecret);

    private static bool Verify(string payload, string signature, string? secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signature)) return false;
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        var expected = Convert.ToHexString(hash).ToLowerInvariant();
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature.ToLowerInvariant()));
    }

    /// <summary>Payment details from Razorpay (method, bank, bank reference, fee). Null when unavailable.</summary>
    public async Task<RzpPayment?> FetchPaymentAsync(string paymentId, CancellationToken ct = default)
    {
        var o = await GetOptionsAsync();
        if (!o.Enabled || string.IsNullOrWhiteSpace(paymentId)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.razorpay.com/v1/payments/{Uri.EscapeDataString(paymentId)}");
        req.Headers.Authorization = Basic(o);
        using var res = await http.SendAsync(req, ct);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<RzpPayment>(ct) : null;
    }

    private class RzpOrder { [JsonPropertyName("id")] public string Id { get; set; } = ""; }
}

public class RzpPayment
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("method")] public string? Method { get; set; }        // upi, card, netbanking, wallet, emi …
    [JsonPropertyName("bank")] public string? Bank { get; set; }            // netbanking bank code, e.g. HDFC
    [JsonPropertyName("wallet")] public string? Wallet { get; set; }
    [JsonPropertyName("vpa")] public string? Vpa { get; set; }
    [JsonPropertyName("card")] public RzpCard? Card { get; set; }
    [JsonPropertyName("fee")] public long? Fee { get; set; }                // paise, incl. GST
    [JsonPropertyName("tax")] public long? Tax { get; set; }
    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }    // unix seconds
    [JsonPropertyName("acquirer_data")] public Dictionary<string, object?>? AcquirerData { get; set; }

    /// <summary>The bank's own reference for the payment (UTR / RRN / bank transaction id).</summary>
    public string? BankReference => new[] { "bank_transaction_id", "rrn", "upi_transaction_id", "auth_code" }
        .Select(k => AcquirerData?.GetValueOrDefault(k)?.ToString()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public WoodsyCart.Domain.PaymentMethod? OurMethod => Method switch
    {
        "upi" => WoodsyCart.Domain.PaymentMethod.Upi, "card" => WoodsyCart.Domain.PaymentMethod.Card,
        "netbanking" => WoodsyCart.Domain.PaymentMethod.NetBanking, "wallet" => WoodsyCart.Domain.PaymentMethod.Wallet,
        "emi" or "cardless_emi" => WoodsyCart.Domain.PaymentMethod.Emi, _ => null
    };

    private static readonly Dictionary<string, string> Banks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HDFC"] = "HDFC Bank", ["ICIC"] = "ICICI Bank", ["SBIN"] = "State Bank of India", ["UTIB"] = "Axis Bank", ["KKBK"] = "Kotak Mahindra Bank",
        ["PUNB_R"] = "Punjab National Bank", ["BARB_R"] = "Bank of Baroda", ["CNRB"] = "Canara Bank", ["UBIN"] = "Union Bank of India",
        ["IDFB"] = "IDFC First Bank", ["YESB"] = "Yes Bank", ["INDB"] = "IndusInd Bank", ["FDRL"] = "Federal Bank", ["SIBL"] = "South Indian Bank"
    };

    /// <summary>"Net banking · HDFC Bank · fee ₹26.55" style summary for the order's payment list.</summary>
    public string Describe()
    {
        var how = Method switch
        {
            "netbanking" => "Net banking" + (Bank is null ? "" : " · " + Banks.GetValueOrDefault(Bank, Bank)),
            "upi" => "UPI" + (Vpa is null ? "" : " · " + Vpa),
            "card" => "Card" + (Card is null ? "" : $" · {Card.Network} •••• {Card.Last4}"),
            "wallet" => "Wallet" + (Wallet is null ? "" : " · " + Wallet),
            "emi" => "EMI", null => "Online", _ => Method
        };
        return $"Razorpay · {how}" + (Fee is > 0 ? $" · gateway fee ₹{Fee.Value / 100m:N2}" : "");
    }
}

public class RzpCard
{
    [JsonPropertyName("network")] public string? Network { get; set; }
    [JsonPropertyName("last4")] public string? Last4 { get; set; }
}
