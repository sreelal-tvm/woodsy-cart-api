using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoodsyCart.Infrastructure.Vendors;

/// <summary>
/// Reads the public catalogue of a DotPe / DotShowroom storefront (used by gbfurniture.in).
/// Only public, unauthenticated catalogue endpoints are used — the same ones the vendor's website calls.
/// </summary>
public class DotShowroomClient(HttpClient http)
{
    public const string BaseUrl = "https://api.dotshowroom.in/api/";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public async Task<List<DsCategoryItems>> GetCatalogAsync(long storeId, CancellationToken ct)
    {
        var res = await http.GetFromJsonAsync<DsStoreItemsResponse>($"dotk/catalog/getItemsBasicDetailsByStoreId/{storeId}?category_type=1", Json, ct);
        return res?.StoreItems ?? [];
    }

    public async Task<DsItem?> GetItemAsync(long storeId, long itemId, CancellationToken ct)
    {
        var res = await http.GetFromJsonAsync<DsEnvelope>($"dotk/vo1/products/item?store_id={storeId}&item_id={itemId}", Json, ct);
        if (res is null || !res.Status || string.IsNullOrEmpty(res.Data)) return null;
        return JsonSerializer.Deserialize<DsItem>(res.Data, Json);
    }
}

public class DsEnvelope
{
    public bool Status { get; set; }
    public string? Message { get; set; }
    public string? Data { get; set; }
}

public class DsStoreItemsResponse
{
    public bool Status { get; set; }
    [JsonPropertyName("store_items")] public List<DsCategoryItems>? StoreItems { get; set; }
}

public class DsCategoryItems
{
    public DsCategory Category { get; set; } = new();
    public List<DsItem>? Items { get; set; }
}

public class DsCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
}

public class DsItem
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    [JsonPropertyName("discounted_price")] public decimal DiscountedPrice { get; set; }
    public DsCategory? Category { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    public List<DsImage>? Images { get; set; }
    public int Available { get; set; }
    [JsonPropertyName("is_active")] public int IsActive { get; set; }
    public string? Description { get; set; }
    [JsonPropertyName("description_detail")] public string? DescriptionDetail { get; set; }
    public List<DsVariant>? Variants { get; set; }
    [JsonPropertyName("variants_count")] public int VariantsCount { get; set; }
    public List<DsTag>? Tags { get; set; }
    public List<DsCollection>? Collections { get; set; }
    public DsTax? Tax { get; set; }
    [JsonPropertyName("brand_name")] public string? BrandName { get; set; }
    [JsonPropertyName("sku_id")] public string? SkuId { get; set; }
    public string? Link { get; set; }
    [JsonPropertyName("item_option_variants")] public List<Dictionary<string, List<DsOptionValue>>>? ItemOptionVariants { get; set; }
}

public class DsImage
{
    [JsonPropertyName("image_id")] public long ImageId { get; set; }
    [JsonPropertyName("image_url")] public string ImageUrl { get; set; } = "";
    [JsonPropertyName("thumbnail_url")] public string? ThumbnailUrl { get; set; }
    [JsonPropertyName("media_type")] public int MediaType { get; set; } = 1;
    public int Status { get; set; } = 1;
    [JsonPropertyName("compressed_images")] public Dictionary<string, string>? CompressedImages { get; set; }
    [JsonPropertyName("alt_text")] public string? AltText { get; set; }

    /// <summary>Picks the ~800px rendition when available (good quality, small size).</summary>
    public string BestUrl()
    {
        if (MediaType != 1 || CompressedImages is null) return ImageUrl;
        var key = CompressedImages.Keys.FirstOrDefault(k => k.StartsWith("800"));
        return key is null ? ImageUrl : CompressedImages[key];
    }
}

public class DsVariant
{
    [JsonPropertyName("variant_id")] public long VariantId { get; set; }
    [JsonPropertyName("variant_name")] public string VariantName { get; set; } = "";
    public int Status { get; set; }
    public int Available { get; set; }
    public decimal Price { get; set; }
    [JsonPropertyName("discounted_price")] public decimal DiscountedPrice { get; set; }
    public List<DsImage>? Images { get; set; }
    [JsonPropertyName("sku_id")] public string? SkuId { get; set; }
    [JsonPropertyName("option_variant_names")] public List<DsOptionValue>? OptionVariantNames { get; set; }
}

public class DsOptionValue
{
    [JsonPropertyName("variant_name_id")] public long VariantNameId { get; set; }
    [JsonPropertyName("option_name")] public string OptionName { get; set; } = "";
    [JsonPropertyName("variant_name")] public string VariantName { get; set; } = "";
    [JsonPropertyName("colour_code")] public string? ColourCode { get; set; }
}

public class DsTag
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("tag_color")] public string? TagColor { get; set; }
}

public class DsCollection
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    public int Rank { get; set; }
}

public class DsTax
{
    [JsonPropertyName("hsn_sac_code")] public string? HsnSacCode { get; set; }
    public decimal Gst { get; set; }
}
