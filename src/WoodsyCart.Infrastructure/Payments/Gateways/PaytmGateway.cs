using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;

namespace WoodsyCart.Infrastructure.Payments.Gateways;

/// <summary>Paytm Payment Gateway: Initiate Transaction → hosted payment page, confirmed with the v3 order status API.</summary>
public class PaytmGateway(HttpClient http, PaymentConfigService config) : PaymentGatewayBase(http, config)
{
    public override string Code => PaymentProviders.PaytmPg;

    private static string Host(bool test) => test ? "https://securegw-stage.paytm.in" : "https://securegw.paytm.in";

    /// <summary>POSTs {"body":…,"head":{"signature":…}} where the signature covers the exact body JSON sent.</summary>
    private async Task<JsonDocument> PostSignedAsync(string url, object body, string merchantKey, CancellationToken ct)
    {
        var bodyJson = JsonSerializer.Serialize(body);
        var payload = $"{{\"body\":{bodyJson},\"head\":{{\"signature\":\"{PaytmChecksum.Generate(bodyJson, merchantKey)}\"}}}}";
        using var res = await Http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        return await ReadJsonAsync(res, "Paytm", ct);
    }

    public override async Task<GatewayLaunch> StartAsync(GatewayOrderContext ctx, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync(forNewPayment: true);
        var o = ctx.Order;
        var mid = v["merchantId"];
        var id = Uri.EscapeDataString(ctx.MerchantOrderId);
        using var doc = await PostSignedAsync($"{Host(test)}/theia/api/v1/initiateTransaction?mid={Uri.EscapeDataString(mid)}&orderId={id}", new
        {
            requestType = "Payment",
            mid,
            websiteName = v.GetValueOrDefault("website") ?? (test ? "WEBSTAGING" : "DEFAULT"),
            orderId = ctx.MerchantOrderId,
            callbackUrl = ctx.ReturnUrl,
            txnAmount = new { value = Money(o.Total), currency = "INR" },
            userInfo = new { custId = $"WC{o.UserId}", mobile = Phone(o.ShipPhone), email = o.ShipEmail, firstName = FirstName(o.ShipName) }
        }, v["merchantKey"], ct);
        var token = Str(doc.RootElement, "body", "txnToken");
        if (Str(doc.RootElement, "body", "resultInfo", "resultStatus") != "S" || string.IsNullOrEmpty(token))
            throw new AppException($"Paytm couldn't start the payment: {Str(doc.RootElement, "body", "resultInfo", "resultMsg") ?? "unknown error"}", 502);
        return new GatewayLaunch("form", $"{Host(test)}/theia/api/v1/showPaymentPage?mid={Uri.EscapeDataString(mid)}&orderId={id}",
            new Dictionary<string, string> { ["mid"] = mid, ["orderId"] = ctx.MerchantOrderId, ["txnToken"] = token });
    }

    private Task<JsonDocument> StatusAsync(bool test, Dictionary<string, string> v, string orderId, CancellationToken ct) =>
        PostSignedAsync($"{Host(test)}/v3/order/status", new { mid = v["merchantId"], orderId }, v["merchantKey"], ct);

    public override async Task<GatewayResult> GetStatusAsync(string merchantOrderId, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        using var doc = await StatusAsync(test, v, merchantOrderId, ct);
        if (!doc.RootElement.TryGetProperty("body", out var b)) return new GatewayResult(GatewayState.Pending);
        var status = Str(b, "resultInfo", "resultStatus");
        if (status != "TXN_SUCCESS")
            return new GatewayResult(status == "TXN_FAILURE" && Str(b, "txnId") is not null ? GatewayState.Failed : GatewayState.Pending);

        var mode = Str(b, "paymentMode")?.ToUpperInvariant();
        var method = mode switch
        {
            "UPI" => PaymentMethod.Upi,
            "CC" or "DC" or "PAYTMCC" => PaymentMethod.Card,
            "NB" => PaymentMethod.NetBanking,
            "PPI" or "BALANCE" or "PAYTM_DIGITAL_CREDIT" => PaymentMethod.Wallet,
            "EMI" => PaymentMethod.Emi,
            _ => (PaymentMethod?)null
        };
        var how = method switch
        {
            PaymentMethod.Upi => "UPI", PaymentMethod.Card => "Card", PaymentMethod.NetBanking => "Net banking",
            PaymentMethod.Wallet => "Paytm wallet", PaymentMethod.Emi => "EMI", _ => "online payment"
        };
        DateTime? at = DateTime.TryParse(Str(b, "txnDate"), out var d) ? DateTime.SpecifyKind(d.AddHours(-5.5), DateTimeKind.Utc) : null; // IST
        return new GatewayResult(GatewayState.Paid, Dec(Str(b, "txnAmount")), Str(b, "txnId") ?? merchantOrderId, method,
            Str(b, "bankTxnId"), $"Paytm · {how}", at);
    }

    public override async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        try
        {
            using var doc = await StatusAsync(test, v, $"wc-test-{Guid.NewGuid():N}", ct);
            var msg = Str(doc.RootElement, "body", "resultInfo", "resultMsg") ?? "no reply";
            var code = Str(doc.RootElement, "body", "resultInfo", "resultCode");
            // Valid credentials: the made-up order is "334 Invalid Order Id". A wrong MID gives "501 System Error",
            // a wrong key a checksum error.
            return code == "334" || msg.Contains("order", StringComparison.OrdinalIgnoreCase)
                ? (true, test ? "Connected to Paytm (staging)." : "Connected to Paytm (live).")
                : (false, $"Paytm didn't accept the merchant ID / key ({code}: {msg}). Check that test mode matches the keys.");
        }
        catch (AppException e) { return (false, e.Message); }
    }
}

/// <summary>Paytm's checksum: AES-128-CBC( SHA-256(params|salt) + salt ) with the merchant key and Paytm's fixed IV.</summary>
public static class PaytmChecksum
{
    private static readonly byte[] Iv = Encoding.ASCII.GetBytes("@@@@&&&&####$$$$");
    private const string SaltChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static string Generate(string body, string merchantKey)
    {
        var salt = new string(Enumerable.Range(0, 4).Select(_ => SaltChars[RandomNumberGenerator.GetInt32(SaltChars.Length)]).ToArray());
        return Encrypt(Hash(body, salt) + salt, merchantKey);
    }

    public static bool Verify(string body, string merchantKey, string checksum)
    {
        try
        {
            var plain = Decrypt(checksum, merchantKey);
            var salt = plain[^4..];
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(plain), Encoding.UTF8.GetBytes(Hash(body, salt) + salt));
        }
        catch (Exception) { return false; }
    }

    private static string Hash(string body, string salt) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{body}|{salt}"))).ToLowerInvariant();

    private static Aes Create(string key)
    {
        var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes(key);
        aes.IV = Iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    private static string Encrypt(string text, string key)
    {
        using var aes = Create(key);
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToBase64String(aes.CreateEncryptor().TransformFinalBlock(bytes, 0, bytes.Length));
    }

    private static string Decrypt(string b64, string key)
    {
        using var aes = Create(key);
        var bytes = Convert.FromBase64String(b64);
        return Encoding.UTF8.GetString(aes.CreateDecryptor().TransformFinalBlock(bytes, 0, bytes.Length));
    }
}
