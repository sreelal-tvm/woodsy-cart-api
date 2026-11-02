namespace WoodsyCart.Infrastructure.Contracts;

public record CategoryDto(int Id, string Name, string Slug, string? ImageUrl, string? Description, int ProductCount);

public record CollectionDto(int Id, string Name, string Slug, string? ImageUrl, int ProductCount);

public record ProductCardDto(
    int Id, string Name, string Slug, string? ImageUrl, string? ThumbnailUrl,
    decimal Mrp, decimal Price, int DiscountPercent,
    decimal RatingAverage, int RatingCount, bool IsAvailable,
    string CategoryName, string CategorySlug, List<string> Tags, bool HasVariants, List<string> Colors,
    decimal RegularPrice = 0, string? OfferName = null, string? OfferSlug = null, string? OfferColor = null, int? StockLeft = null,
    bool SellingFast = false, bool Pick = false);

/// <summary>
/// An offer as shown to customers. StartsAt/EndsAt are the window running now (or the next one when upcoming), so flash deals count
/// down to the end of today's window; Schedule describes a repeating deal ("Every Monday, 8:00 pm for 15 minutes").
/// </summary>
public record OfferDto(int Id, string Name, string Slug, string? Tagline, string? Description, string? BannerImageUrl, string ThemeColor,
    DateTime StartsAt, DateTime EndsAt, string Status, int DiscountType, decimal Value, decimal? MaxDiscount, string? CouponCode,
    int ProductCount, List<ProductCardDto> Products, bool Flash = false, string? Schedule = null);

/// <summary>The offer applied to a product on its detail page.</summary>
public record ProductOfferDto(string Name, string Slug, string? Tagline, string ThemeColor, DateTime EndsAt, string? CouponCode, bool Flash = false);

/// <summary>A flash deal that will cover this product within the next day: shown as "Flash deal at 8:00 pm — ₹X".</summary>
public record UpcomingDealDto(string Name, string Slug, string ThemeColor, DateTime StartsAt, DateTime EndsAt, decimal Price);

public record ImageDto(int Id, string Url, string? ThumbnailUrl, string? AltText, int MediaType, int? VariantId);

public record OptionValueDto(int Id, string Value, string? ColorCode);

public record OptionDto(int Id, string Name, List<OptionValueDto> Values, bool IsColour = false);

/// <summary>StockLeft: "Only N left" (see CatalogService.StockLeft); the storefront shows it at or below the Admin setting.</summary>
public record VariantDto(int Id, string Name, decimal Mrp, decimal Price, bool IsAvailable, List<int> OptionValueIds, decimal RegularPrice = 0, int? StockLeft = null);

public record ProductDetailDto(
    int Id, string Name, string Slug, string? Description, string? DescriptionHtml,
    decimal Mrp, decimal Price, int DiscountPercent, bool IsAvailable,
    decimal RatingAverage, int RatingCount,
    CategoryDto Category, string? Brand, string? Sku, string? HsnCode, decimal GstRate,
    string? Material, string? Dimensions, string? Warranty, int DeliveryDays, bool AssemblyIncluded,
    List<ImageDto> Images, List<OptionDto> Options, List<VariantDto> Variants,
    List<string> Tags, List<CollectionDto> Collections, string? MetaTitle, string? MetaDescription,
    decimal RegularPrice = 0, ProductOfferDto? Offer = null, UpcomingDealDto? UpcomingDeal = null, int? StockLeft = null,
    bool SellingFast = false, bool Pick = false);

public class ProductQuery
{
    public string? Search { get; set; }
    public string? Category { get; set; }
    public string? Collection { get; set; }
    public string? Tag { get; set; }
    /// <summary>Offer slug: only products currently in that offer.</summary>
    public string? Offer { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public string? Colors { get; set; }      // comma separated
    public string? Sizes { get; set; }       // comma separated
    public bool? InStock { get; set; }
    public bool? Featured { get; set; }
    public int? MinDiscount { get; set; }
    public string Sort { get; set; } = "popular";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 24;
}

public record FacetItem(string Key, string Label, int Count, string? Extra = null);

public record FacetsDto(decimal PriceMin, decimal PriceMax, List<FacetItem> Categories, List<FacetItem> Colors, List<FacetItem> Sizes, List<FacetItem> Collections);

public record BannerDto(int Id, string Title, string? Subtitle, string ImageUrl, string? LinkUrl, string? ButtonText);

public record HomeDto(
    List<BannerDto> Banners, List<CategoryDto> Categories,
    List<ProductCardDto> Featured, List<ProductCardDto> NewArrivals,
    List<ProductCardDto> BestDeals, List<CollectionDto> Collections, List<OfferDto>? Offers = null);

public record ReviewDto(int Id, string UserName, int Rating, string? Title, string? Comment, bool IsVerifiedPurchase, DateTime CreatedAt);

public record CreateReviewRequest(int Rating, string? Title, string? Comment);

/// <summary>Product page: can this customer review the product? Only buyers whose order was delivered can.</summary>
public record MyReviewStatusDto(bool CanReview, string? OrderNumber, bool Reviewed);

public record PublicSettingsDto(
    string BusinessName, string? Tagline, string Phone, string? WhatsApp, string Email,
    string Address, decimal ShippingFee, decimal FreeShippingThreshold,
    bool CodEnabled, decimal CodFee, decimal CodMaxOrderAmount, bool OnlinePaymentEnabled, string? RazorpayKeyId,
    string? InstagramUrl, string? FacebookUrl, string? YoutubeUrl, int DefaultDeliveryDays, string? MapsApiKey = null, string? SupportHours = null,
    bool VisitStats = false, string? ThreadsUrl = null, int LowStockAt = 0, WelcomeTeaserDto? WelcomeOffer = null,
    string? PickLabel = null);

/// <summary>The welcome offer as advertised to visitors: "5% off your first order (up to ₹1,500)", valid for Hours after signing up.</summary>
public record WelcomeTeaserDto(string Text, int Hours, decimal MinOrder);

/// <summary>NearBengaluru: not on the delivery list, but the pinned spot is close enough that the customer should ask us on WhatsApp.</summary>
public record PincodeCheckDto(string Pincode, bool Serviceable, int DeliveryDays, decimal ExtraCharge, bool CodAvailable, string? Area, string Message, bool NearBengaluru = false);

public record EnquiryRequest(string Name, string? Email, string Phone, string? Subject, string Message, int? ProductId);
