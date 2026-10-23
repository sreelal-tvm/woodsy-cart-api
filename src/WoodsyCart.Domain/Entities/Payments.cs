using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

/// <summary>Known payment providers. Stored as strings so new ones need no migration.</summary>
public static class PaymentProviders
{
    public const string DirectUpi = "DirectUpi";        // customer pays our UPI ID from their own UPI app — no gateway
    public const string BankTransfer = "BankTransfer";  // NEFT/IMPS to our bank account, verified manually / by alert
    public const string CashOnDelivery = "CashOnDelivery";
    public const string Razorpay = "Razorpay";
    public const string PhonePe = "PhonePe";
    public const string Cashfree = "Cashfree";
    public const string PayU = "PayU";
    public const string PaytmPg = "PaytmPg";

    /// <summary>Gateways that hand the customer to a hosted checkout and confirm payment by signature/webhook.</summary>
    public static readonly string[] Gateways = [Razorpay, PhonePe, Cashfree, PayU, PaytmPg];

    /// <summary>Providers whose checkout flow is implemented in the app (others can be configured but not used yet).</summary>
    public static readonly string[] Implemented = [DirectUpi, BankTransfer, CashOnDelivery, Razorpay, PhonePe, Cashfree, PayU, PaytmPg];
}

/// <summary>Credentials and options for one provider, edited in Admin → Payment integrations.</summary>
public class PaymentProviderConfig : BaseEntity
{
    public string Code { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsTestMode { get; set; } = true;
    /// <summary>Non-secret settings as JSON (UPI ID, payee name, key id…).</summary>
    public string SettingsJson { get; set; } = "{}";
    /// <summary>Secret settings as JSON, encrypted with ASP.NET Data Protection.</summary>
    public string? ProtectedSecrets { get; set; }
}

/// <summary>A payment method shown at checkout and the provider that processes it.</summary>
public class PaymentMethodOption : BaseEntity
{
    public PaymentMethod Method { get; set; }
    public bool IsEnabled { get; set; }
    public string ProviderCode { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
}

public enum PaymentAlertStatus { Unmatched = 0, Matched = 1, Duplicate = 2, Ignored = 3, NotACredit = 4 }

/// <summary>
/// A "money received" message (bank SMS, UPI app notification or e-mail) forwarded from the owner's phone,
/// used to confirm direct-UPI and bank-transfer payments without a gateway.
/// </summary>
public class PaymentAlert : BaseEntity
{
    public string Source { get; set; } = "sms";
    public string RawText { get; set; } = "";
    public decimal? Amount { get; set; }
    public string? Utr { get; set; }
    public string? Sender { get; set; }
    public PaymentAlertStatus Status { get; set; }
    public int? OrderId { get; set; }
    public Order? Order { get; set; }
    public string? Note { get; set; }
}
