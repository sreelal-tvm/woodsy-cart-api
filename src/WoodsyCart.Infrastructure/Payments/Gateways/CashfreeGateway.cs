using System.Net.Http.Json;
using System.Text.Json;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;

namespace WoodsyCart.Infrastructure.Payments.Gateways;

/// <summary>Cashfree Payments PG (API version 2023-08-01): create order → payment session for the JS checkout.</summary>
public class CashfreeGateway(HttpClient http, PaymentConfigService config) : PaymentGatewayBase(http, config)
{
    public override string Code => PaymentProviders.Cashfree;

    private static string Base(bool test) => test ? "https://sandbox.cashfree.com/pg" : "https://api.cashfree.com/pg";

    private static HttpRequestMessage Request(HttpMethod method, string url, Dictionary<string, string> v)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("x-client-id", v["appId"]);
        req.Headers.Add("x-client-secret", v["secretKey"]);
        req.Headers.Add("x-api-version", "2023-08-01");
        return req;
    }

    public override async Task<GatewayLaunch> StartAsync(GatewayOrderContext ctx, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync(forNewPayment: true);
        var o = ctx.Order;
        using var req = Request(HttpMethod.Post, $"{Base(test)}/orders", v);
        req.Content = JsonContent.Create(new
        {
            order_id = ctx.MerchantOrderId,
            order_amount = o.Total,
            order_currency = "INR",
            order_note = $"WoodsyCart order {o.OrderNumber}",
            customer_details = new
            {
                customer_id = $"WC{o.UserId}",
                customer_name = o.ShipName,
                customer_email = o.ShipEmail,
                customer_phone = Phone(o.ShipPhone)
            },
            order_meta = new { return_url = ctx.ReturnUrl, notify_url = ctx.NotifyUrl }
        });
        using var res = await Http.SendAsync(req, ct);
        using var doc = await ReadJsonAsync(res, "Cashfree", ct);
        var session = Str(doc.RootElement, "payment_session_id");
        if (!res.IsSuccessStatusCode || string.IsNullOrEmpty(session))
            throw new AppException($"Cashfree couldn't start the payment: {Str(doc.RootElement, "message") ?? res.StatusCode.ToString()}", 502);
        return new GatewayLaunch("cashfree", "https://sdk.cashfree.com/js/v3/cashfree.js", SessionId: session, Mode: test ? "sandbox" : "production");
    }

    public override async Task<GatewayResult> GetStatusAsync(string merchantOrderId, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        var id = Uri.EscapeDataString(merchantOrderId);
        using var req = Request(HttpMethod.Get, $"{Base(test)}/orders/{id}", v);
        using var res = await Http.SendAsync(req, ct);
        using var doc = await ReadJsonAsync(res, "Cashfree", ct);
        var status = Str(doc.RootElement, "order_status");
        if (status != "PAID")
            return new GatewayResult(status is "EXPIRED" or "TERMINATED" ? GatewayState.Failed : GatewayState.Pending);
        var amount = Dec(Str(doc.RootElement, "order_amount"));

        // Payment details (method, bank reference) — best effort.
        using var preq = Request(HttpMethod.Get, $"{Base(test)}/orders/{id}/payments", v);
        using var pres = await Http.SendAsync(preq, ct);
        string? paymentId = null, group = null, bankRef = null; DateTime? at = null;
        if (pres.IsSuccessStatusCode)
        {
            using var pdoc = await ReadJsonAsync(pres, "Cashfree", ct);
            if (pdoc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var p in pdoc.RootElement.EnumerateArray())
                {
                    if (Str(p, "payment_status") != "SUCCESS") continue;
                    paymentId = Str(p, "cf_payment_id");
                    group = Str(p, "payment_group");
                    bankRef = Str(p, "bank_reference");
                    if (DateTime.TryParse(Str(p, "payment_completion_time"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)) at = t;
                    break;
                }
        }
        var method = group switch
        {
            "upi" => PaymentMethod.Upi,
            "credit_card" or "debit_card" or "prepaid_card" => PaymentMethod.Card,
            "net_banking" => PaymentMethod.NetBanking,
            "wallet" => PaymentMethod.Wallet,
            "cardless_emi" or "credit_card_emi" or "debit_card_emi" or "pay_later" => PaymentMethod.Emi,
            _ => (PaymentMethod?)null
        };
        var how = group is null ? "online payment" : group.Replace('_', ' ');
        return new GatewayResult(GatewayState.Paid, amount, paymentId ?? merchantOrderId, method, bankRef, $"Cashfree · {how}", at);
    }

    public override async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        using var req = Request(HttpMethod.Get, $"{Base(test)}/orders/wc-connection-test-{Guid.NewGuid():N}", v);
        using var res = await Http.SendAsync(req, ct);
        // An unknown order with accepted keys gives 404; wrong keys give 401.
        return res.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.OK
            ? (true, test ? "Connected to Cashfree (sandbox)." : "Connected to Cashfree (live).")
            : (false, $"Cashfree rejected the App ID / secret key ({(int)res.StatusCode}). Check test mode matches the keys.");
    }
}
