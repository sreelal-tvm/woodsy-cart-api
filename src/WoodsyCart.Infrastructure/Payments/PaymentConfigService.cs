using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Payments;

public record ProviderField(string Key, string Label, bool Secret = false, string? Placeholder = null, string? Help = null, bool Required = true);

public record ProviderDefinition(string Code, string Name, string Description, string[] Methods, ProviderField[] Fields, string? DocsUrl, string? FeeNote);

public record ProviderFieldValue(string Key, string Label, bool Secret, string? Placeholder, string? Help, bool Required, string? Value, bool HasValue);

public record ProviderView(string Code, string Name, string Description, string[] Methods, bool Implemented, bool IsEnabled, bool IsTestMode,
    bool IsConfigured, List<ProviderFieldValue> Fields, string? DocsUrl, string? FeeNote);

public record ProviderUpdate(bool IsEnabled, bool IsTestMode, Dictionary<string, string?> Values);

public record MethodOptionDto(PaymentMethod Method, bool IsEnabled, string ProviderCode, string Label, string? Description, int SortOrder, string[] AllowedProviders);

public record MethodOptionUpdate(PaymentMethod Method, bool IsEnabled, string ProviderCode, string Label, string? Description, int SortOrder);

public record CheckoutMethodDto(PaymentMethod Method, string Label, string? Description, string Provider);

/// <summary>
/// Payment providers and checkout payment methods configured in Admin → Payment integrations.
/// Secrets (API keys) are encrypted at rest with ASP.NET Data Protection.
/// </summary>
public partial class PaymentConfigService(AppDbContext db, IDataProtectionProvider dataProtection)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("WoodsyCart.PaymentSecrets.v1");
    private const string Mask = "••••••••";

    public static readonly ProviderDefinition[] Definitions =
    [
        new(PaymentProviders.DirectUpi, "Direct UPI (no gateway)",
            "Customers pay your business UPI ID from Google Pay, PhonePe, Paytm, BHIM or any UPI app. Each order gets a unique amount (a few paise off) so payments are matched automatically from your bank's credit alerts.",
            ["Upi"],
            [
                new("upiId", "Business UPI ID", Placeholder: "woodsycart@ybl", Help: "Use a merchant UPI ID from PhonePe Business, Paytm for Business, Google Pay for Business or BharatPe — not a personal ID."),
                new("payeeName", "Payee name shown in the UPI app", Placeholder: "WoodsyCart"),
                new("uniquePaise", "Unique paise per order (true/false)", Placeholder: "true", Help: "Reduces each order by 1–99 paise so the payment amount identifies the order.", Required: false),
                new("alertKey", "Payment alert key", Secret: true, Help: "Secret used by the forwarder app on your phone. Generate one below.", Required: false)
            ],
            null, "₹0 — UPI payments to a business QR are free up to ₹1 lakh/month (0.4% above ₹2,000 beyond that)."),
        new(PaymentProviders.BankTransfer, "Bank transfer (NEFT / IMPS)",
            "Show your bank details at checkout. Payments are confirmed from bank alerts or when you verify the customer's UTR.",
            ["BankTransfer"],
            [
                new("accountName", "Account holder name"),
                new("accountNumber", "Account number"),
                new("ifsc", "IFSC", Placeholder: "HDFC0001234"),
                new("bankName", "Bank & branch", Required: false)
            ],
            null, "₹0"),
        new(PaymentProviders.CashOnDelivery, "Cash on delivery",
            "Customer pays the delivery person. Limits and COD fee are in Settings → Delivery & payments.",
            ["CashOnDelivery"], [], null, "₹0 (collection risk on made-to-order furniture)"),
        new(PaymentProviders.Razorpay, "Razorpay",
            "Cards, net banking, wallets, EMI and UPI through Razorpay's hosted checkout.",
            ["Upi", "Card", "NetBanking", "Wallet", "Emi"],
            [
                new("keyId", "Key ID", Placeholder: "rzp_live_…"),
                new("keySecret", "Key secret", Secret: true),
                new("webhookSecret", "Webhook secret", Secret: true, Help: "Webhook URL: /api/payments/razorpay/webhook (event payment.captured)", Required: false)
            ],
            "https://razorpay.com/docs/payments/dashboard/account-settings/api-keys/", "About 2% + GST per payment (standard plan)."),
        new(PaymentProviders.PhonePe, "PhonePe Payment Gateway",
            "PhonePe Standard Checkout: customers pay by UPI, cards or net banking on PhonePe's page and come back to their order.",
            ["Upi", "Card", "NetBanking", "Wallet"],
            [
                new("clientId", "Client ID"),
                new("clientVersion", "Client version", Placeholder: "1"),
                new("clientSecret", "Client secret", Secret: true,
                    Help: "Optional webhook in the PhonePe dashboard: /api/payments/phonepe/webhook. Payments are also checked automatically every few minutes.")
            ],
            "https://developer.phonepe.com/", "Check current offer on PhonePe's pricing page."),
        new(PaymentProviders.Cashfree, "Cashfree Payments",
            "Cashfree checkout: UPI, cards, net banking, wallets and EMI on Cashfree's secure page.",
            ["Upi", "Card", "NetBanking", "Wallet", "Emi"],
            [new("appId", "App ID"), new("secretKey", "Secret key", Secret: true,
                Help: "Notifications are sent to /api/payments/cashfree/webhook automatically. Use sandbox keys with test mode on.")],
            "https://www.cashfree.com/docs/", null),
        new(PaymentProviders.PayU, "PayU",
            "PayU hosted checkout: UPI, cards, net banking, wallets and EMI.",
            ["Upi", "Card", "NetBanking", "Wallet", "Emi"],
            [new("merchantKey", "Merchant key"), new("salt", "Salt (v1)", Secret: true,
                Help: "Optional webhook in the PayU dashboard: /api/payments/payu/webhook. Use test keys with test mode on.")],
            "https://docs.payu.in/", null),
        new(PaymentProviders.PaytmPg, "Paytm Payment Gateway",
            "Paytm checkout: UPI, cards, net banking and Paytm wallet on Paytm's secure page.",
            ["Upi", "Card", "NetBanking", "Wallet"],
            [new("merchantId", "Merchant ID"),
             new("website", "Website name", Placeholder: "DEFAULT", Help: "WEBSTAGING for staging (test mode) keys, DEFAULT for live keys.", Required: false),
             new("merchantKey", "Merchant key", Secret: true,
                Help: "Optional webhook in the Paytm dashboard: /api/payments/paytmpg/webhook.")],
            "https://developer.paytm.com/", null)
    ];

    private static readonly (PaymentMethod method, string provider, string label, string description, bool enabled)[] DefaultMethods =
    [
        (PaymentMethod.Upi, PaymentProviders.DirectUpi, "UPI", "Google Pay, PhonePe, Paytm, BHIM or any UPI app", true),
        (PaymentMethod.Card, PaymentProviders.Razorpay, "Credit / debit card", "Visa, Mastercard, RuPay", false),
        (PaymentMethod.NetBanking, PaymentProviders.Razorpay, "Net banking", "All major Indian banks", false),
        (PaymentMethod.Wallet, PaymentProviders.Razorpay, "Wallets", "Paytm, Amazon Pay and more", false),
        (PaymentMethod.Emi, PaymentProviders.Razorpay, "EMI", "Card and cardless EMI", false),
        (PaymentMethod.BankTransfer, PaymentProviders.BankTransfer, "Bank transfer", "NEFT / IMPS to our account", false),
        (PaymentMethod.CashOnDelivery, PaymentProviders.CashOnDelivery, "Cash on delivery", "Pay when your furniture arrives", false)
    ];

    // ---------- Seeding ----------

    public async Task EnsureDefaultsAsync()
    {
        var existing = await db.PaymentProviderConfigs.Select(c => c.Code).ToListAsync();
        foreach (var d in Definitions.Where(d => !existing.Contains(d.Code)))
        {
            var cfg = new PaymentProviderConfig { Code = d.Code, IsTestMode = true, IsEnabled = d.Code == PaymentProviders.DirectUpi };
            if (d.Code == PaymentProviders.DirectUpi)
            {
                cfg.SettingsJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["payeeName"] = "WoodsyCart", ["uniquePaise"] = "true" });
                cfg.ProtectedSecrets = _protector.Protect(JsonSerializer.Serialize(new Dictionary<string, string> { ["alertKey"] = NewKey() }));
            }
            db.PaymentProviderConfigs.Add(cfg);
        }

        var methods = await db.PaymentMethodOptions.Select(m => m.Method).ToListAsync();
        var firstSeed = methods.Count == 0;
        var order = 0;
        foreach (var m in DefaultMethods)
        {
            order++;
            if (methods.Contains(m.method)) continue;
            db.PaymentMethodOptions.Add(new PaymentMethodOption
            {
                Method = m.method, ProviderCode = m.provider, Label = m.label, Description = m.description, IsEnabled = m.enabled, SortOrder = order
            });
        }
        // Cash on delivery starts switched off (made-to-order furniture); it can be enabled in Payment integrations.
        if (firstSeed)
            await db.BusinessSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.CodEnabled, false));
        await db.SaveChangesAsync();
    }

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    // ---------- Reading ----------

    /// <summary>All settings of a provider (secrets decrypted) — for server-side use only.</summary>
    public async Task<(bool enabled, bool testMode, Dictionary<string, string> values)> GetAsync(string code)
    {
        var cfg = await db.PaymentProviderConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code);
        if (cfg is null) return (false, true, []);
        return (cfg.IsEnabled, cfg.IsTestMode, Merge(cfg));
    }

    private Dictionary<string, string> Merge(PaymentProviderConfig cfg)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(cfg.SettingsJson) ?? [];
        foreach (var (k, v) in Secrets(cfg)) values[k] = v;
        return values;
    }

    private Dictionary<string, string> Secrets(PaymentProviderConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.ProtectedSecrets)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(_protector.Unprotect(cfg.ProtectedSecrets)) ?? []; }
        catch (CryptographicException) { return []; } // keys rotated/lost — admin must re-enter secrets
    }

    public static bool IsConfigured(ProviderDefinition d, IReadOnlyDictionary<string, string> values) =>
        d.Fields.Where(f => f.Required).All(f => values.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v));

    public async Task<List<ProviderView>> GetProvidersAsync()
    {
        var configs = await db.PaymentProviderConfigs.AsNoTracking().ToListAsync();
        return Definitions.Select(d =>
        {
            var cfg = configs.FirstOrDefault(c => c.Code == d.Code);
            var values = cfg is null ? new Dictionary<string, string>() : Merge(cfg);
            var fields = d.Fields.Select(f =>
            {
                var has = values.TryGetValue(f.Key, out var v) && !string.IsNullOrEmpty(v);
                // The alert key is needed to set up the forwarder app, so it is shown in full; other secrets are masked.
                var shown = !has ? null : f.Secret && f.Key != "alertKey" ? Mask + v![^Math.Min(4, v!.Length)..] : v;
                return new ProviderFieldValue(f.Key, f.Label, f.Secret, f.Placeholder, f.Help, f.Required, shown, has);
            }).ToList();
            return new ProviderView(d.Code, d.Name, d.Description, d.Methods, PaymentProviders.Implemented.Contains(d.Code),
                cfg?.IsEnabled ?? false, cfg?.IsTestMode ?? true, IsConfigured(d, values), fields, d.DocsUrl, d.FeeNote);
        }).ToList();
    }

    // ---------- Writing ----------

    [GeneratedRegex(@"^[a-zA-Z0-9.\-_]{2,256}@[a-zA-Z][a-zA-Z0-9]{1,64}$")]
    public static partial Regex VpaPattern();

    public async Task UpdateProviderAsync(string code, ProviderUpdate update)
    {
        var def = Definitions.FirstOrDefault(d => d.Code == code) ?? throw new AppException("Unknown payment provider.", 404);
        var cfg = await db.PaymentProviderConfigs.FirstOrDefaultAsync(c => c.Code == code);
        if (cfg is null) { cfg = new PaymentProviderConfig { Code = code }; db.PaymentProviderConfigs.Add(cfg); }

        var plain = JsonSerializer.Deserialize<Dictionary<string, string>>(cfg.SettingsJson) ?? [];
        var secrets = Secrets(cfg);
        foreach (var f in def.Fields)
        {
            if (!update.Values.TryGetValue(f.Key, out var raw)) continue;
            var value = raw?.Trim() ?? "";
            if (f.Secret)
            {
                if (value.StartsWith(Mask)) continue; // unchanged masked value
                if (value.Length == 0) secrets.Remove(f.Key); else secrets[f.Key] = value;
            }
            else if (value.Length == 0) plain.Remove(f.Key);
            else plain[f.Key] = value;
        }

        if (code == PaymentProviders.DirectUpi && plain.TryGetValue("upiId", out var vpa) && !VpaPattern().IsMatch(vpa))
            throw new AppException("That doesn't look like a valid UPI ID (for example woodsycart@ybl).");

        var merged = new Dictionary<string, string>(plain);
        foreach (var (k, v) in secrets) merged[k] = v;
        if (update.IsEnabled && !IsConfigured(def, merged))
            throw new AppException($"Fill in all required {def.Name} fields before enabling it.");

        cfg.IsEnabled = update.IsEnabled;
        cfg.IsTestMode = update.IsTestMode;
        cfg.SettingsJson = JsonSerializer.Serialize(plain);
        cfg.ProtectedSecrets = secrets.Count == 0 ? null : _protector.Protect(JsonSerializer.Serialize(secrets));
        await db.SaveChangesAsync();
    }

    public async Task<string> RegenerateAlertKeyAsync()
    {
        var cfg = await db.PaymentProviderConfigs.FirstAsync(c => c.Code == PaymentProviders.DirectUpi);
        var secrets = Secrets(cfg);
        secrets["alertKey"] = NewKey();
        cfg.ProtectedSecrets = _protector.Protect(JsonSerializer.Serialize(secrets));
        await db.SaveChangesAsync();
        return secrets["alertKey"];
    }

    // ---------- Checkout methods ----------

    public async Task<List<MethodOptionDto>> GetMethodsAsync() =>
        (await db.PaymentMethodOptions.AsNoTracking().OrderBy(m => m.SortOrder).ToListAsync())
        .Select(m => new MethodOptionDto(m.Method, m.IsEnabled, m.ProviderCode, m.Label, m.Description, m.SortOrder,
            Definitions.Where(d => d.Methods.Contains(m.Method.ToString())).Select(d => d.Code).ToArray()))
        .ToList();

    public async Task UpdateMethodsAsync(List<MethodOptionUpdate> updates)
    {
        var options = await db.PaymentMethodOptions.ToListAsync();
        foreach (var u in updates)
        {
            var o = options.FirstOrDefault(x => x.Method == u.Method) ?? throw new AppException($"Unknown payment method {u.Method}.");
            var def = Definitions.FirstOrDefault(d => d.Code == u.ProviderCode && d.Methods.Contains(u.Method.ToString()))
                      ?? throw new AppException($"{u.ProviderCode} can't process {u.Label}.");
            if (u.IsEnabled && !PaymentProviders.Implemented.Contains(def.Code))
                throw new AppException($"{def.Name} checkout isn't built yet — choose another provider for {u.Label}.");
            o.IsEnabled = u.IsEnabled; o.ProviderCode = u.ProviderCode; o.SortOrder = u.SortOrder;
            o.Label = string.IsNullOrWhiteSpace(u.Label) ? o.Label : u.Label.Trim();
            o.Description = u.Description?.Trim();
        }
        // Keep the COD switch used by checkout limits in sync.
        var cod = options.First(o => o.Method == PaymentMethod.CashOnDelivery).IsEnabled;
        await db.BusinessSettings.ExecuteUpdateAsync(s => s.SetProperty(x => x.CodEnabled, cod));
        await db.SaveChangesAsync();
    }

    /// <summary>Payment methods a customer can choose right now: enabled, routed to an enabled, implemented and configured provider.</summary>
    public async Task<List<CheckoutMethodDto>> GetCheckoutMethodsAsync()
    {
        var methods = await db.PaymentMethodOptions.AsNoTracking().Where(m => m.IsEnabled).OrderBy(m => m.SortOrder).ToListAsync();
        var configs = await db.PaymentProviderConfigs.AsNoTracking().ToListAsync();
        var result = new List<CheckoutMethodDto>();
        foreach (var m in methods)
        {
            if (!await IsUsableAsync(m.ProviderCode, configs)) continue;
            result.Add(new CheckoutMethodDto(m.Method, m.Label, m.Description, m.ProviderCode));
        }
        return result;
    }

    private Task<bool> IsUsableAsync(string code, List<PaymentProviderConfig> configs)
    {
        if (!PaymentProviders.Implemented.Contains(code)) return Task.FromResult(false);
        if (code == PaymentProviders.CashOnDelivery) return Task.FromResult(true);
        var cfg = configs.FirstOrDefault(c => c.Code == code);
        var def = Definitions.First(d => d.Code == code);
        return Task.FromResult(cfg is { IsEnabled: true } && IsConfigured(def, Merge(cfg)));
    }

    /// <summary>The provider that will process a checkout payment method, or an error if it isn't offered.</summary>
    public async Task<string> ResolveProviderAsync(PaymentMethod method)
    {
        // Older clients send "Razorpay" for any online payment.
        if (method == PaymentMethod.Razorpay) return PaymentProviders.Razorpay;
        var available = await GetCheckoutMethodsAsync();
        return available.FirstOrDefault(m => m.Method == method)?.Provider
               ?? throw new AppException("This payment method isn't available right now. Please choose another one.");
    }
}
