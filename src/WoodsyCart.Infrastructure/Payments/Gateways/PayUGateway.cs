using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;

namespace WoodsyCart.Infrastructure.Payments.Gateways;

/// <summary>PayU India hosted checkout: hashed form POST to /_payment, confirmed with the verify_payment API.</summary>
public class PayUGateway(HttpClient http, PaymentConfigService config) : PaymentGatewayBase(http, config)
{
    public override string Code => PaymentProviders.PayU;

    private static string PayUrl(bool test) => test ? "https://test.payu.in/_payment" : "https://secure.payu.in/_payment";
    private static string InfoUrl(bool test) => test ? "https://test.payu.in/merchant/postservice?form=2" : "https://info.payu.in/merchant/postservice?form=2";

    private static string Sha512(string s) => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    public override async Task<GatewayLaunch> StartAsync(GatewayOrderContext ctx, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync(forNewPayment: true);
        var o = ctx.Order;
        var f = new Dictionary<string, string>
        {
            ["key"] = v["merchantKey"],
            ["txnid"] = ctx.MerchantOrderId,
            ["amount"] = Money(o.Total),
            ["productinfo"] = $"WoodsyCart order {o.OrderNumber}",
            ["firstname"] = FirstName(o.ShipName),
            ["email"] = o.ShipEmail ?? "",
            ["phone"] = Phone(o.ShipPhone),
            ["surl"] = ctx.ReturnUrl,
            ["furl"] = ctx.ReturnUrl
        };
        // key|txnid|amount|productinfo|firstname|email|udf1|udf2|udf3|udf4|udf5||||||SALT
        f["hash"] = Sha512(string.Join('|', f["key"], f["txnid"], f["amount"], f["productinfo"], f["firstname"], f["email"],
            "", "", "", "", "", "", "", "", "", "", v["salt"]));
        return new GatewayLaunch("form", PayUrl(test), f);
    }

    private async Task<JsonDocument> VerifyAsync(bool test, Dictionary<string, string> v, string txnId, CancellationToken ct)
    {
        const string command = "verify_payment";
        using var res = await Http.PostAsync(InfoUrl(test), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["key"] = v["merchantKey"], ["command"] = command, ["var1"] = txnId,
            ["hash"] = Sha512($"{v["merchantKey"]}|{command}|{txnId}|{v["salt"]}")
        }), ct);
        return await ReadJsonAsync(res, "PayU", ct);
    }

    public override async Task<GatewayResult> GetStatusAsync(string merchantOrderId, CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        using var doc = await VerifyAsync(test, v, merchantOrderId, ct);
        if (!doc.RootElement.TryGetProperty("transaction_details", out var all) || all.ValueKind != JsonValueKind.Object
            || !all.TryGetProperty(merchantOrderId, out var t))
            return new GatewayResult(GatewayState.Pending);

        var status = (Str(t, "status") ?? "").ToLowerInvariant();
        if (status != "success")
            return new GatewayResult(status is "failure" or "failed" or "usercancelled" or "dropped" or "bounced" ? GatewayState.Failed : GatewayState.Pending);

        var mode = Str(t, "mode")?.ToUpperInvariant();
        var method = mode switch
        {
            "UPI" => PaymentMethod.Upi,
            "CC" or "DC" or "CREDITCARD" or "DEBITCARD" => PaymentMethod.Card,
            "NB" => PaymentMethod.NetBanking,
            "CASH" or "WALLET" => PaymentMethod.Wallet,
            "EMI" or "BNPL" => PaymentMethod.Emi,
            _ => (PaymentMethod?)null
        };
        var how = method switch
        {
            PaymentMethod.Upi => "UPI", PaymentMethod.Card => "Card", PaymentMethod.NetBanking => "Net banking",
            PaymentMethod.Wallet => "Wallet", PaymentMethod.Emi => "EMI", _ => "online payment"
        };
        var amount = Dec(Str(t, "amt")) ?? Dec(Str(t, "transaction_amount"));
        DateTime? at = DateTime.TryParse(Str(t, "addedon"), out var d) ? DateTime.SpecifyKind(d.AddHours(-5.5), DateTimeKind.Utc) : null; // IST
        return new GatewayResult(GatewayState.Paid, amount, Str(t, "mihpayid") ?? merchantOrderId, method, Str(t, "bank_ref_num"), $"PayU · {how}", at);
    }

    public override async Task<(bool ok, string message)> TestAsync(CancellationToken ct = default)
    {
        var (test, v) = await SettingsAsync();
        try
        {
            using var doc = await VerifyAsync(test, v, $"wctest{DateTime.UtcNow:HHmmss}", ct);
            var msg = Str(doc.RootElement, "msg") ?? "";
            return msg.Contains("invalid", StringComparison.OrdinalIgnoreCase) || msg.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
                ? (false, $"PayU rejected the merchant key / salt: {msg}")
                : (true, test ? "Connected to PayU (test)." : "Connected to PayU (live).");
        }
        catch (AppException e) { return (false, e.Message); }
    }
}
