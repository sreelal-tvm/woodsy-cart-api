using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Services;

public record OptionValueEdit(int? Id, string Value, string? ColorCode, bool IsHidden, int? CopyFromValueId);
public record OptionEdit(int? Id, string Name, bool IsColour, List<OptionValueEdit> Values);

/// <summary>
/// Product options (Size, Colour …): naming rules shared with the vendor sync, and the Admin editor.
/// Vendor names and colour codes are always kept (VendorValue / VendorColorCode); Admin edits are flagged so the sync keeps them.
/// </summary>
public static partial class ProductOptions
{
    [GeneratedRegex(@"colou?r|shade", RegexOptions.IgnoreCase)]
    private static partial Regex ColourWord();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();

    /// <summary>"Colour", "Color", "Wood Colour", "Name of Colour", "Table Color", "Name Of Shade" … all mean colour.</summary>
    public static bool IsColourName(string name) => ColourWord().IsMatch(name);

    /// <summary>The name customers see for a vendor option: a product's only colour option is simply "Colour".</summary>
    public static string DefaultName(string vendorName, int colourOptionsOnProduct) =>
        IsColourName(vendorName) && colourOptionsOnProduct == 1 ? "Colour" : vendorName;

    public static string? NormaliseColour(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        if (!c.StartsWith('#')) c = "#" + c;
        return HexColour().IsMatch(c) ? c.ToLowerInvariant() : throw new AppException($"\"{code}\" is not a colour code like #8b5a2b.");
    }

    /// <summary>Customer-facing variant name from its option values, e.g. "6x5ft Walnut".</summary>
    public static string DisplayName(ProductVariant v) =>
        string.Join(" ", v.OptionValues.OrderBy(o => o.Option?.Position ?? 0).Select(o => o.Value));

    /// <summary>
    /// Variant names follow Admin edits: vendor variants keep the vendor's name until one of their values is renamed;
    /// variants created in Admin are always named from their values.
    /// </summary>
    public static void RefreshVariantNames(Product p)
    {
        foreach (var v in p.Variants.Where(v => v.OptionValues.Count > 0))
        {
            var custom = v.VendorVariantId is null || v.OptionValues.Any(o => o.IsCustomized || o.VendorValueId is null);
            var name = custom ? DisplayName(v) : v.VendorName ?? v.Name;
            if (!string.IsNullOrWhiteSpace(name) && v.Name != name) v.Name = name;
        }
    }

    /// <summary>What to tell the vendor: their own variant name, plus any option chosen in Admin that they don't list.</summary>
    public static string VendorFacingName(ProductVariant v, Product p)
    {
        if (v.VendorVariantId is not null) return v.VendorName ?? v.Name;
        var src = v.SourceVariantId is null ? null : p.Variants.FirstOrDefault(x => x.Id == v.SourceVariantId);
        if (src is null) return v.Name;
        var changed = v.OptionValues.Where(o => !src.OptionValues.Contains(o)).Select(o => $"{o.Option?.Name ?? "Option"}: {o.Value}").ToList();
        var basis = src.VendorName ?? src.Name;
        return changed.Count == 0 ? basis : $"{basis} — but {string.Join(", ", changed)}";
    }
}

/// <summary>Saves the option editor on the Admin product page.</summary>
public class ProductOptionsService(AppDbContext db, PricingService pricing)
{
    public async Task SaveAsync(int productId, List<OptionEdit> edits)
    {
        var p = await db.Products.Include(x => x.Category)
                    .Include(x => x.Options).ThenInclude(o => o.Values)
                    .Include(x => x.Variants).ThenInclude(v => v.OptionValues)
                    .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == productId)
                ?? throw new AppException("Product not found", 404);
        Validate(edits);

        // Options removed in the editor
        foreach (var opt in p.Options.Where(o => !edits.Any(e => e.Id == o.Id)).ToList())
        {
            if (opt.Values.Any(v => v.VendorValueId is not null))
                throw new AppException($"\"{opt.Name}\" comes from the vendor and can't be removed — remove the values you don't sell instead.");
            foreach (var val in opt.Values.ToList()) await RemoveValueAsync(p, opt, val);
            p.Options.Remove(opt);
            db.ProductOptions.Remove(opt);
        }

        for (var i = 0; i < edits.Count; i++)
        {
            var e = edits[i];
            var opt = e.Id is > 0 ? p.Options.FirstOrDefault(o => o.Id == e.Id) ?? throw new AppException("Option not found — reload the page.") : null;
            if (opt is null) { AddOption(p, e, i); continue; }

            // Option name / colour setting / order
            var positionChanged = opt.Position != i;
            opt.Name = e.Name.Trim(); opt.IsColour = e.IsColour; opt.Position = i;
            if (opt.VendorName is null) opt.IsCustomized = true;
            else
            {
                // Once changed in Admin, the sync keeps Admin's name, order and colour setting.
                var colours = p.Options.Count(o => ProductOptions.IsColourName(o.VendorName ?? o.Name));
                var differs = opt.Name != ProductOptions.DefaultName(opt.VendorName, colours) || opt.IsColour != ProductOptions.IsColourName(opt.VendorName);
                opt.IsCustomized |= differs || positionChanged;
            }

            // Values removed in the editor
            foreach (var val in opt.Values.Where(v => !e.Values.Any(x => x.Id == v.Id)).ToList())
                await RemoveValueAsync(p, opt, val);

            for (var j = 0; j < e.Values.Count; j++)
            {
                var ve = e.Values[j];
                var val = ve.Id is > 0 ? opt.Values.FirstOrDefault(v => v.Id == ve.Id) ?? throw new AppException("Value not found — reload the page.") : null;
                if (val is null) { AddValue(p, opt, ve, j); continue; }
                val.Value = ve.Value.Trim();
                val.ColorCode = ProductOptions.NormaliseColour(ve.ColorCode);
                val.SortOrder = j;
                val.IsCustomized = val.VendorValueId is null || val.Value != val.VendorValue || val.ColorCode != val.VendorColorCode;
                if (val.IsHidden != ve.IsHidden) SetHidden(p, val, ve.IsHidden);
            }
        }

        ProductOptions.RefreshVariantNames(p);
        PricingService.Apply(p, p.Category, await pricing.GetSettingsAsync());
        p.UpdatedAt = DateTime.UtcNow; // lets the offer-price worker pick up new variants
        await db.SaveChangesAsync();
    }

    private static void Validate(List<OptionEdit> edits)
    {
        foreach (var e in edits)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) throw new AppException("Every option needs a name.");
            if (e.Values.Count == 0) throw new AppException($"\"{e.Name}\" needs at least one value.");
            if (e.Values.Any(v => string.IsNullOrWhiteSpace(v.Value))) throw new AppException($"A value of \"{e.Name}\" is empty.");
            var dup = e.Values.Where(v => !v.IsHidden).GroupBy(v => v.Value.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) throw new AppException($"\"{dup.Key}\" appears twice in \"{e.Name}\".");
            foreach (var v in e.Values) ProductOptions.NormaliseColour(v.ColorCode);
        }
        var dupOpt = edits.GroupBy(e => e.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupOpt is not null) throw new AppException($"There are two options called \"{dupOpt.Key}\".");
    }

    /// <summary>
    /// A new option. On a product with variants, its first value is added to every existing variant and each further value
    /// becomes a copy of them; on a product without variants, every value becomes a variant at the product's price.
    /// </summary>
    private void AddOption(Product p, OptionEdit e, int position)
    {
        var opt = new ProductOption { Name = e.Name.Trim(), IsColour = e.IsColour, Position = position, IsCustomized = true };
        p.Options.Add(opt);
        var values = e.Values.Select((ve, j) => new ProductOptionValue
        {
            Option = opt, Value = ve.Value.Trim(), ColorCode = ProductOptions.NormaliseColour(ve.ColorCode), SortOrder = j, IsCustomized = true
        }).ToList();
        foreach (var v in values) opt.Values.Add(v);

        var existing = p.Variants.ToList();
        if (existing.Count == 0)
        {
            var sort = 0;
            foreach (var v in values)
            {
                var variant = new ProductVariant
                {
                    VendorMrp = p.VendorMrp, VendorPrice = p.VendorPrice, Mrp = p.Mrp, SellingPrice = p.SellingPrice,
                    IsAvailable = true, IsActive = true, SortOrder = sort++
                };
                variant.OptionValues.Add(v);
                p.Variants.Add(variant);
            }
            return;
        }
        foreach (var variant in existing) variant.OptionValues.Add(values[0]);
        foreach (var v in values.Skip(1)) CloneVariants(p, values[0], v);
    }

    /// <summary>A new value on an existing option: copies the variants of the chosen (or first) value, including their prices.</summary>
    private void AddValue(Product p, ProductOption opt, OptionValueEdit ve, int sortOrder)
    {
        var val = new ProductOptionValue { Option = opt, Value = ve.Value.Trim(), ColorCode = ProductOptions.NormaliseColour(ve.ColorCode), SortOrder = sortOrder, IsCustomized = true };
        opt.Values.Add(val);
        var source = (ve.CopyFromValueId is > 0 ? opt.Values.FirstOrDefault(v => v.Id == ve.CopyFromValueId) : null)
                     ?? opt.Values.Where(v => v.Id > 0 && !v.IsHidden).OrderBy(v => v.SortOrder).FirstOrDefault();
        if (source is not null) CloneVariants(p, source, val);
    }

    private static void CloneVariants(Product p, ProductOptionValue from, ProductOptionValue to)
    {
        var sort = p.Variants.Count == 0 ? 0 : p.Variants.Max(v => v.SortOrder) + 1;
        foreach (var src in p.Variants.Where(v => v.OptionValues.Contains(from)).ToList())
        {
            var copy = new ProductVariant
            {
                // Follow the vendor variant at the root, so prices and stock stay in step with the vendor.
                SourceVariantId = src.Id > 0 ? (src.VendorVariantId is not null ? src.Id : src.SourceVariantId ?? src.Id) : src.SourceVariantId,
                VendorMrp = src.VendorMrp, VendorPrice = src.VendorPrice, Mrp = src.Mrp, SellingPrice = src.SellingPrice,
                IsAvailable = src.IsAvailable, IsActive = src.IsActive, SortOrder = sort++
            };
            foreach (var ov in src.OptionValues.Where(o => o != from)) copy.OptionValues.Add(ov);
            copy.OptionValues.Add(to);
            p.Variants.Add(copy);
        }
    }

    /// <summary>Vendor values are hidden (the sync would add them back); values added in Admin are deleted with their variants.</summary>
    private async Task RemoveValueAsync(Product p, ProductOption opt, ProductOptionValue val)
    {
        if (val.VendorValueId is not null) { if (!val.IsHidden) SetHidden(p, val, true); return; }

        foreach (var v in p.Variants.Where(v => v.OptionValues.Contains(val)).ToList())
        {
            var isCopy = v.VendorVariantId is null && v.SourceVariantId is not null;
            if (!isCopy) { v.OptionValues.Remove(val); continue; } // e.g. the first value of an added option on a vendor variant
            if (v.Id > 0 && await db.OrderItems.AnyAsync(i => i.VariantId == v.Id))
            {
                v.IsActive = false; // ordered before — keep for the order history
                v.OptionValues.Clear();
                continue;
            }
            if (v.Id > 0) await db.CartItems.Where(c => c.VariantId == v.Id).ExecuteDeleteAsync();
            v.OptionValues.Clear();
            p.Variants.Remove(v);
            db.ProductVariants.Remove(v);
        }
        opt.Values.Remove(val);
        db.ProductOptionValues.Remove(val);
    }

    private static void SetHidden(Product p, ProductOptionValue val, bool hidden)
    {
        val.IsHidden = hidden;
        foreach (var v in p.Variants.Where(v => v.OptionValues.Contains(val)))
            v.IsActive = !hidden && !v.OptionValues.Any(o => o != val && o.IsHidden); // the next sync re-applies the vendor's own on/off
    }
}
