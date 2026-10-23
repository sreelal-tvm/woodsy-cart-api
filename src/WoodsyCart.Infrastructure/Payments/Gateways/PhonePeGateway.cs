using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;

namespace WoodsyCart.Infrastructure.Payments.Gateways;

/// <summary>PhonePe Payment Gateway, Standard Checkout (v2): OAuth token, create payment, order status.</summary>
public class PhonePeGateway(HttpClient http, PaymentConfigService config) : PaymentGatewayBase(http, config)
{
    public override string Code => PaymentProviders.PhonePe;

    private static readonly ConcurrentDictionary<string, (string token, DateTimeOffset expires)> Tokens = new();

    private static string Base(bool test) => test ? "https://api-preprod.phonepe.com/apis/pg-sandbox" : "https://api.phonepe.com/apis/pg";
    private static string AuthUrl(bool test) => test
        ? "https://api-preprod.phonepe.com/apis/pg-sandbox/v1/oauth/token"
        : "https://api.phonepe.com/apis/identity-manager/v1/oauth/token";

    private async Task<string> TokenAsync(bool test, Dictionary<string, string> v, CancellationToken ct)
    {
        var key = $"{test}|{v["clientId"]}|{v.GetValueOrDefault("clientVersion")}";
        if (Tokens.TryGetValue(key, out var t) && t.expires > DateTimeOffset.UtcNow.AddMinutes(2)) return t.token;
        using var res = await Http.PostAsync(AuthUrl(test), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = v["clientId"],
            ["client_version"] = v.GetValueOrDefault("clientVersion") ?? "1",
            ["client_secret"] = v["clientSecret"],
            ["grant_type"] = "client_credentials"
        }), ct);
        using var doc = await ReadJsonAsync(res, "PhonePe", ct);
        var token = Str(doc.RootElement, "access_token");
        if (!res.IsSuccessStatusCode || string.IsNullOrEmpty(token))
            throw new AppException($"PhonePe rejected the client ID / secret ({(int)res.StatusCode}).", 502);
        var exp = long.TryParse(Str(doc.RootElement, "expires_at"), out var e) ? DateTimeOffset.FromUnixTimeSeconds(e) : DateTimeOffset.UtcNow.AddMinutes(30);
        Tokens[key] = (token, exp);
        return token;
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string url, bool test, Dictionary<string, string> v, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("O-Bearer", await TokenAsync(test, v, ct));
        return req;
    }

    public override async Task<GatewayLaunch> StartAsync(GatewayOrderContext ctx, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync(forNewPayment: true);
        using var req = await RequestAsync(HttpMethod.Post, $"{Base(test)}/checkout/v2/pay", test, v, ct);
        req.Content = JsonContent.Create(new
        {
            merchantOrderId = ctx.MerchantOrderId,
            amount = (long)Math.Round(ctx.Order.Total * 100),
            expireAfter = 1800,
            metaInfo = new { udf1 = ctx.Order.OrderNumber },
            paymentFlow = new
            {
                type = "PG_CHECKOUT",
                message = $"WoodsyCart order {ctx.Order.OrderNumber}",
                merchantUrls = new { redirectUrl = ctx.ReturnUrl }
            }
        });
        using var res = await Http.SendAsync(req, ct);
        using var doc = await ReadJsonAsync(res, "PhonePe", ct);
        var url = Str(doc.RootElement, "redirectUrl");
        if (!res.IsSuccessStatusCode || string.IsNullOrEmpty(url))
            throw new AppException($"PhonePe couldn't start the payment: {Str(doc.RootElement, "message") ?? res.StatusCode.ToString()}", 502);
        return new GatewayLaunch("redirect", url);
    }

    public override async Task<GatewayResult> GetStatusAsync(string merchantOrderId, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        using var req = await RequestAsync(HttpMethod.Get, $"{Base(test)}/checkout/v2/order/{Uri.EscapeDataString(merchantOrderId)}/status?details=false", test, v, ct);
        using var res = await Http.SendAsync(req, ct);
        using var doc = await ReadJsonAsync(res, "PhonePe", ct);
        var root = doc.RootElement;
        var state = Str(root, "state");
        if (state != "COMPLETED")
            return new GatewayResult(state == "FAILED" ? GatewayState.Failed : GatewayState.Pending);

        JsonElement? paid = null;
        if (root.TryGetProperty("paymentDetails", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var p in list.EnumerateArray())
                if (Str(p, "state") == "COMPLETED") { paid = p; break; }

        var mode = paid is { } pd ? Str(pd, "paymentMode") : null;
        var method = mode switch
        {
            { } m when m.StartsWith("UPI") => PaymentMethod.Upi,
            "CARD" or "CREDIT_CARD" or "DEBIT_CARD" => PaymentMethod.Card,
            "NET_BANKING" => PaymentMethod.NetBanking,
            "WALLET" => PaymentMethod.Wallet,
            _ => (PaymentMethod?)null
        };
        var utr = paid is { } p2 ? Str(p2, "rail", "utr") ?? Str(p2, "rail", "upiTransactionId") : null;
        var at = paid is { } p3 && long.TryParse(Str(p3, "timestamp"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null;
        return new GatewayResult(GatewayState.Paid, Dec(Str(root, "amount")) / 100m,
            (paid is { } p4 ? Str(p4, "transactionId") : null) ?? Str(root, "orderId"),
            method, utr, $"PhonePe · {Describe(mode)}", at);
    }

    private static string Describe(string? mode) => mode switch
    {
        null => "online payment",
        { } m when m.StartsWith("UPI") => "UPI",
        "NET_BANKING" => "Net banking",
        "CARD" or "CREDIT_CARD" or "DEBIT_CARD" => "Card",
        _ => mode.Replace('_', ' ').ToLowerInvariant()
    };

    public override async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        try
        {
            Tokens.Clear();
            await TokenAsync(test, v, ct);
            return (true, test ? "Connected to PhonePe (sandbox)." : "Connected to PhonePe (live).");
        }
        catch (AppException e) { return (false, e.Message); }
    }
}
