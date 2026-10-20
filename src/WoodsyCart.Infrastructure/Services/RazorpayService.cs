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

    private class RzpOrder { [JsonPropertyName("id")] public string Id { get; set; } = ""; }
}
