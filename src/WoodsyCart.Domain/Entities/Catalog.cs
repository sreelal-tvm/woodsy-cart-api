using WoodsyCart.Domain.Common;

namespace WoodsyCart.Domain.Entities;

public class Category : BaseEntity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public bool ShowOnHome { get; set; } = true;
    /// <summary>Overrides the global commission for products in this category.</summary>
    public decimal? CommissionPercent { get; set; }
    public long? VendorCategoryId { get; set; }
    public ICollection<Product> Products { get; set; } = [];
}

public class Collection : BaseEntity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? ImageUrl { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public long? VendorCollectionId { get; set; }
    public ICollection<Product> Products { get; set; } = [];
}

public class Tag : BaseEntity
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#3874ff";
    public long? VendorTagId { get; set; }
    public ICollection<Product> Products { get; set; } = [];
}

public class Product : BaseEntity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? DescriptionHtml { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public string? Brand { get; set; }
    public string? Sku { get; set; }
    public string? HsnCode { get; set; }
    public decimal GstRate { get; set; } = 18;

    // Furniture specifics
    public string? Material { get; set; }
    public string? Dimensions { get; set; }
    public string? Warranty { get; set; }
    public int DeliveryDays { get; set; } = 7;
    public bool AssemblyIncluded { get; set; } = true;

    // Vendor pricing (cost side)
    public decimal VendorMrp { get; set; }
    public decimal VendorPrice { get; set; }
    public decimal? PreviousVendorPrice { get; set; }
    public bool VendorPriceChanged { get; set; }

    // Our pricing (sell side)
    public decimal Mrp { get; set; }
    public decimal SellingPrice { get; set; }
    public decimal? CommissionPercentOverride { get; set; }
    /// <summary>When set, SellingPrice is fixed manually and not recalculated from commission.</summary>
    public bool ManualPrice { get; set; }
    /// <summary>Festival offer currently applied (kept up to date by OfferService); OfferPrice replaces SellingPrice while set.</summary>
    public int? OfferId { get; set; }
    public Offer? Offer { get; set; }
    public decimal? OfferPrice { get; set; }

    public bool IsActive { get; set; } = true;       // published on storefront
    public bool IsAvailable { get; set; } = true;    // vendor stock availability
    public bool IsFeatured { get; set; }
    public bool IsDeleted { get; set; }
    public int SortOrder { get; set; }

    public decimal RatingAverage { get; set; }
    public int RatingCount { get; set; }
    public int SoldCount { get; set; }

    public ProductSource Source { get; set; } = ProductSource.Vendor;
    public long? VendorProductId { get; set; }
    public string? VendorUrl { get; set; }
    /// <summary>When true, vendor sync only refreshes cost/availability and keeps our name/description/images.</summary>
    public bool LockContent { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    public string? MetaTitle { get; set; }
    public string? MetaDescription { get; set; }

    public ICollection<ProductImage> Images { get; set; } = [];
    public ICollection<ProductOption> Options { get; set; } = [];
    public ICollection<ProductVariant> Variants { get; set; } = [];
    public ICollection<Collection> Collections { get; set; } = [];
    public ICollection<Tag> Tags { get; set; } = [];
    public ICollection<Review> Reviews { get; set; } = [];
}

public class ProductImage : BaseEntity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int? VariantId { get; set; }
    public string Url { get; set; } = "";          // source (vendor CDN or uploaded)
    public string? ThumbnailUrl { get; set; }
    public string? LocalPath { get; set; }          // downloaded copy, e.g. /media/products/12/a.webp
    public string? LocalThumbnailPath { get; set; }
    public string? AltText { get; set; }
    public MediaType MediaType { get; set; } = MediaType.Image;
    public int SortOrder { get; set; }
    public long? VendorImageId { get; set; }

    // Vendor-mark clean-up (originals are always kept)
    public ImageCleanStatus CleanStatus { get; set; }
    public string? OriginalPath { get; set; }
    public string? OriginalThumbnailPath { get; set; }
    public string? CleanReport { get; set; }
    public DateTime? CleanedAt { get; set; }
}

public class ProductOption : BaseEntity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public string Name { get; set; } = "";   // Size, Colour
    public int Position { get; set; }
    /// <summary>Shown as colour swatches and used by the colour filter (Colour, Color, Wood Colour, Name of Shade …).</summary>
    public bool IsColour { get; set; }
    /// <summary>The option's name at the vendor; the sync matches on it, so the shown Name can be changed.</summary>
    public string? VendorName { get; set; }
    /// <summary>Name, order or colour setting changed in Admin — the vendor sync leaves them alone.</summary>
    public bool IsCustomized { get; set; }
    public ICollection<ProductOptionValue> Values { get; set; } = [];
}

public class ProductOptionValue : BaseEntity
{
    public int ProductOptionId { get; set; }
    public ProductOption? Option { get; set; }
    public string Value { get; set; } = "";
    public string? ColorCode { get; set; }
    public long? VendorValueId { get; set; }
    /// <summary>Exactly as the vendor sends it (kept up to date by the sync, for "reset to vendor").</summary>
    public string? VendorValue { get; set; }
    public string? VendorColorCode { get; set; }
    /// <summary>Name or colour changed in Admin — the vendor sync no longer overwrites them.</summary>
    public bool IsCustomized { get; set; }
    /// <summary>Removed in Admin. Vendor values are hidden rather than deleted so the next sync doesn't add them back.</summary>
    public bool IsHidden { get; set; }
    public int SortOrder { get; set; }
    public ICollection<ProductVariant> Variants { get; set; } = [];
}

public class ProductVariant : BaseEntity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public string Name { get; set; } = "";
    /// <summary>The variant's name at the vendor — used on purchase orders so the vendor knows what to make.</summary>
    public string? VendorName { get; set; }
    /// <summary>Variants added in Admin (e.g. a new colour) copy prices and stock from this vendor variant on every sync.</summary>
    public int? SourceVariantId { get; set; }
    public string? Sku { get; set; }
    public decimal VendorMrp { get; set; }
    public decimal VendorPrice { get; set; }
    public decimal Mrp { get; set; }
    public decimal SellingPrice { get; set; }
    /// <summary>Price under the product's current offer (see Product.OfferId).</summary>
    public decimal? OfferPrice { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public long? VendorVariantId { get; set; }
    public ICollection<ProductOptionValue> OptionValues { get; set; } = [];
}

public class Review : BaseEntity
{
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public int Rating { get; set; }
    public string? Title { get; set; }
    public string? Comment { get; set; }
    public bool IsApproved { get; set; }
    public bool IsVerifiedPurchase { get; set; }
    /// <summary>The delivered order the review was written from (null for older reviews).</summary>
    public int? OrderId { get; set; }
    public Order? Order { get; set; }
}

public class Banner : BaseEntity
{
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string ImageUrl { get; set; } = "";
    public string? LinkUrl { get; set; }
    public string? ButtonText { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A time-limited sale such as "Diwali Offers": a discount on all products, chosen categories or chosen products.</summary>
public class Offer : BaseEntity
{
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    /// <summary>Short line shown on banners and badges, e.g. "Up to 15% off on sofas".</summary>
    public string? Tagline { get; set; }
    public string? Description { get; set; }
    public string? BannerImageUrl { get; set; }
    /// <summary>Accent colour for the banner and badges (#rrggbb).</summary>
    public string ThemeColor { get; set; } = "#e8590c";
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool ShowOnHome { get; set; } = true;
    public int SortOrder { get; set; }

    public DiscountType DiscountType { get; set; }
    public decimal Value { get; set; }
    /// <summary>Percent offers: the most a single unit can be reduced by (₹).</summary>
    public decimal? MaxDiscount { get; set; }
    /// <summary>Margin over our GST-inclusive cost the offer price may never go below.</summary>
    public decimal MinMarginPercent { get; set; } = 5;
    public OfferScope Scope { get; set; }
    /// <summary>Optional coupon code advertised with the offer (the coupon itself is set up under Coupons).</summary>
    public string? CouponCode { get; set; }

    public ICollection<Category> Categories { get; set; } = [];
    public ICollection<Product> Products { get; set; } = [];
}
