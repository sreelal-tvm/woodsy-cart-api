using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

public class AppUser : BaseEntity
{
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Customer;
    public bool IsActive { get; set; } = true;
    /// <summary>Created by guest checkout; has no password until the customer registers with the same email.</summary>
    public bool IsGuest { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public string? Notes { get; set; }
    /// <summary>Forgot password: SHA-256 of the emailed one-time token (the token itself is never stored).</summary>
    public string? PasswordResetTokenHash { get; set; }
    public DateTime? PasswordResetExpiresAt { get; set; }
    public DateTime? PasswordResetRequestedAt { get; set; }
    /// <summary>When the password was last set, and by whom ("Customer" or the admin's name).</summary>
    public DateTime? PasswordChangedAt { get; set; }
    public string? PasswordChangedBy { get; set; }
    public ICollection<Address> Addresses { get; set; } = [];
    public ICollection<Order> Orders { get; set; } = [];
}

public class Address : BaseEntity
{
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public string Label { get; set; } = "Home";
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Line1 { get; set; } = "";
    public string? Line2 { get; set; }
    public string? Landmark { get; set; }
    public string City { get; set; } = "Bengaluru";
    public string State { get; set; } = "Karnataka";
    public string Pincode { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>Exact delivery point picked on the map (optional).</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public class CartItem : BaseEntity
{
    public int UserId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int? VariantId { get; set; }
    public ProductVariant? Variant { get; set; }
    public int Quantity { get; set; } = 1;
}

public class WishlistItem : BaseEntity
{
    public int UserId { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    /// <summary>Price and stock last seen for price-drop / back-in-stock app notifications.</summary>
    public decimal? SeenPrice { get; set; }
    public bool SeenAvailable { get; set; } = true;
}

public class Enquiry : BaseEntity
{
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string Phone { get; set; } = "";
    public string? Subject { get; set; }
    public string Message { get; set; } = "";
    public int? ProductId { get; set; }
    public Product? Product { get; set; }
    public EnquiryStatus Status { get; set; } = EnquiryStatus.New;
    public string? AdminNotes { get; set; }
}

public class ServiceablePincode : BaseEntity
{
    public string Pincode { get; set; } = "";
    public string City { get; set; } = "Bengaluru";
    public string? Area { get; set; }
    public int DeliveryDays { get; set; } = 7;
    /// <summary>Latest day shown to customers ("8–10 days"). Empty = DeliveryDays + 3.</summary>
    public int? DeliveryDaysMax { get; set; }
    public decimal ExtraCharge { get; set; }
    public bool CodAvailable { get; set; } = true;
    public bool IsActive { get; set; } = true;
}
