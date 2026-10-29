using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

public class Vendor : BaseEntity
{
    public string Name { get; set; } = "";
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? WhatsApp { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Gstin { get; set; }
    public string? Website { get; set; }
    public string? BankDetails { get; set; }
    public string? PaymentTerms { get; set; }
    /// <summary>DotPe/DotShowroom store id used for catalog sync.</summary>
    public long? ExternalStoreId { get; set; }
    public bool SyncEnabled { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>The order WoodsyCart places with the vendor for a customer order (drop-ship).</summary>
public class PurchaseOrder : BaseEntity
{
    public string PoNumber { get; set; } = "";
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public decimal ExpectedCost { get; set; }
    public string? VendorReference { get; set; }
    public string? Notes { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? ExpectedDispatchDate { get; set; }
}

/// <summary>Bill/invoice received from the vendor.</summary>
public class VendorBill : BaseEntity
{
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public int? OrderId { get; set; }
    public Order? Order { get; set; }
    public int? PurchaseOrderId { get; set; }
    public string BillNumber { get; set; } = "";
    public DateTime BillDate { get; set; } = DateTime.UtcNow.Date;
    public DateTime? DueDate { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal GstAmount { get; set; }
    public decimal ShippingCharges { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public BillPaymentStatus PaymentStatus { get; set; } = BillPaymentStatus.Unpaid;
    public DateTime? LastPaidOn { get; set; }
    public string? PaymentReference { get; set; }
    public string? FileUrl { get; set; }
    public string? Notes { get; set; }
}

/// <summary>Tax invoice (or bill of supply) WoodsyCart issues to the customer.</summary>
public class CustomerInvoice : BaseEntity
{
    public string InvoiceNumber { get; set; } = "";
    public string FinancialYear { get; set; } = "";
    public int Sequence { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public DateTime InvoiceDate { get; set; } = DateTime.UtcNow;
    public bool IsTaxInvoice { get; set; }
    public string BillToName { get; set; } = "";
    public string BillToAddress { get; set; } = "";
    public string? BillToPhone { get; set; }
    public string? BillToEmail { get; set; }
    public string? BillToGstin { get; set; }
    public string PlaceOfSupply { get; set; } = "";
    public bool IsInterState { get; set; }
    public decimal TaxableAmount { get; set; }
    public decimal Cgst { get; set; }
    public decimal Sgst { get; set; }
    public decimal Igst { get; set; }
    public decimal Discount { get; set; }
    public decimal ShippingFee { get; set; }
    public decimal OtherCharges { get; set; }
    public decimal RoundOff { get; set; }
    public decimal Total { get; set; }
    public bool IsCancelled { get; set; }
    public string? Notes { get; set; }
    public ICollection<CustomerInvoiceLine> Lines { get; set; } = [];
}

public class CustomerInvoiceLine : BaseEntity
{
    public int CustomerInvoiceId { get; set; }
    public string Description { get; set; } = "";
    public string? HsnCode { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }    // GST inclusive
    public decimal Discount { get; set; }
    public decimal TaxableValue { get; set; }
    public decimal GstRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
}

public class Expense : BaseEntity
{
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public ExpenseCategory Category { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }        // excluding GST
    public decimal GstAmount { get; set; }
    public string? PaidTo { get; set; }
    public string? PaymentMode { get; set; }
    public string? Reference { get; set; }
    public int? OrderId { get; set; }
    public string? ReceiptUrl { get; set; }
}

public class VendorSyncLog : BaseEntity
{
    public int VendorId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public SyncStatus Status { get; set; }
    public int ProductsFound { get; set; }
    public int ProductsAdded { get; set; }
    public int ProductsUpdated { get; set; }
    public int PriceChanges { get; set; }
    public int ProductsDeactivated { get; set; }
    public int ImagesDownloaded { get; set; }
    public string? Message { get; set; }
}

public class BusinessSettings : BaseEntity
{
    public string BusinessName { get; set; } = "WoodsyCart";
    public string? LegalName { get; set; }
    public string? Tagline { get; set; }
    public bool GstRegistered { get; set; }
    public string? Gstin { get; set; }
    public string? Pan { get; set; }
    public string AddressLine1 { get; set; } = "";
    public string? AddressLine2 { get; set; }
    public string City { get; set; } = "Bengaluru";
    public string State { get; set; } = "Karnataka";
    public string StateCode { get; set; } = "29";
    public string Pincode { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? WhatsApp { get; set; }
    public string? SupportHours { get; set; } = "Mon–Sat, 10am–7pm";   // shown under the phone number on Contact us; empty = not shown
    public string Email { get; set; } = "";
    public string? Website { get; set; }
    public string? LogoUrl { get; set; }

    public string InvoicePrefix { get; set; } = "WC";
    public string? InvoiceTerms { get; set; }
    public string? BankDetails { get; set; }
    public string? UpiId { get; set; }

    public decimal DefaultCommissionPercent { get; set; } = 15;
    /// <summary>GST % applied to products that have no rate of their own (new manual products, vendor items without a rate).</summary>
    public decimal DefaultGstRate { get; set; } = 18;
    /// <summary>False when the vendor quotes prices before GST (GB Furniture adds GST on top). Our cost is then vendor price + GST.</summary>
    public bool VendorPricesIncludeGst { get; set; }
    public PriceRounding PriceRounding { get; set; } = PriceRounding.EndWith99;

    public decimal ShippingFee { get; set; }
    public decimal FreeShippingThreshold { get; set; }
    public bool CodEnabled { get; set; } = true;
    public decimal CodFee { get; set; }
    public decimal CodMaxOrderAmount { get; set; } = 50000;
    public bool RestrictToServiceablePincodes { get; set; } = true;
    /// <summary>Show the Google map / location search on address forms.</summary>
    public bool MapsEnabled { get; set; }
    /// <summary>Google Maps Platform browser key (public by design — restrict it to your domains in Google Cloud).</summary>
    public string? GoogleMapsApiKey { get; set; }
    public int DefaultDeliveryDays { get; set; } = 7;

    // Product photo clean-up (Admin → Image clean-up)
    public bool ImageCleanAuto { get; set; } = true;          // clean new photos from vendor sync and uploads
    public bool ImageCleanRemoveMarks { get; set; } = true;   // vendor name, website, price, phone
    public bool ImageCleanRecentre { get; set; } = true;      // studio shots: white background, centred product
    public bool ImageCleanEnhance { get; set; } = true;       // room shots: gentle exposure / colour correction

    public string? InstagramUrl { get; set; }
    public string? FacebookUrl { get; set; }
    public string? YoutubeUrl { get; set; }
}
