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
    public decimal ExtraCharge { get; set; }
    public bool CodAvailable { get; set; } = true;
    public bool IsActive { get; set; } = true;
}
