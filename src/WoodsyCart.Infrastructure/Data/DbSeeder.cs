using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Data;

public class DbSeeder(AppDbContext db, AuthService auth, IConfiguration config, ILogger<DbSeeder> logger, Payments.PaymentConfigService payments)
{
    public async Task SeedAsync()
    {
        await db.Database.MigrateAsync();

        if (!await db.BusinessSettings.AnyAsync())
        {
            db.BusinessSettings.Add(new BusinessSettings
            {
                BusinessName = "WoodsyCart",
                Tagline = "Factory-direct furniture, delivered across Bengaluru",
                AddressLine1 = "Bengaluru",
                City = "Bengaluru", State = "Karnataka", StateCode = "29", Pincode = "560001",
                Email = "support@woodsycart.com",
                InvoicePrefix = "WC",
                DefaultCommissionPercent = 15,
                PriceRounding = PriceRounding.EndWith99,
                CodEnabled = true, CodMaxOrderAmount = 25000,
                DefaultDeliveryDays = 7,
                InvoiceTerms = "1. Goods once delivered will be exchanged/returned only as per our return policy (within 7 days of delivery for manufacturing defects).\n" +
                               "2. Warranty is provided by the manufacturer as mentioned on the product page.\n" +
                               "3. Subject to Bengaluru jurisdiction."
            });
        }

        var adminEmail = config["Seed:AdminEmail"] ?? "admin@woodsycart.com";
        if (!await db.Users.AnyAsync(u => u.Role == UserRole.Admin))
        {
            var password = config["Seed:AdminPassword"];
            if (string.IsNullOrWhiteSpace(password))
                logger.LogWarning("Seed:AdminPassword is not configured — admin user was not created.");
            else
            {
                var admin = new AppUser { FullName = "WoodsyCart Admin", Email = adminEmail, Role = UserRole.Admin };
                admin.PasswordHash = auth.HashPassword(admin, password);
                db.Users.Add(admin);
            }
        }

        if (!await db.Vendors.AnyAsync())
        {
            db.Vendors.Add(new Vendor
            {
                Name = "GB Furniture",
                Website = "https://www.gbfurniture.in",
                Address = "Gundur Village, Rampura Gundur Main Road, Virgo Post, Bidarahalli",
                City = "Bengaluru", State = "Karnataka",
                Gstin = "24AALCG3747J1ZC",
                ExternalStoreId = 5145296,
                SyncEnabled = true,
                PaymentTerms = "Pay on dispatch"
            });
        }

        if (!await db.ServiceablePincodes.AnyAsync())
        {
            foreach (var (pin, area) in BengaluruPincodes())
                db.ServiceablePincodes.Add(new ServiceablePincode { Pincode = pin, Area = area, City = "Bengaluru", DeliveryDays = 7 });
        }

        if (!await db.Coupons.AnyAsync())
        {
            db.Coupons.Add(new Coupon
            {
                Code = "WELCOME5", Description = "5% off your first order (up to ₹1,500)",
                DiscountType = DiscountType.Percent, Value = 5, MaxDiscount = 1500, MinOrderAmount = 5000, PerUserLimit = 1
            });
        }

        await db.SaveChangesAsync();
        await payments.EnsureDefaultsAsync();
    }

    private static IEnumerable<(string pin, string? area)> BengaluruPincodes()
    {
        var names = new Dictionary<string, string>
        {
            ["560001"] = "MG Road", ["560002"] = "City Market", ["560003"] = "Malleshwaram", ["560004"] = "Basavanagudi",
            ["560005"] = "Fraser Town", ["560008"] = "HAL", ["560010"] = "Rajajinagar", ["560011"] = "Jayanagar",
            ["560016"] = "Ramamurthy Nagar", ["560017"] = "Vimanapura", ["560020"] = "Seshadripuram", ["560022"] = "Yeshwanthpur",
            ["560024"] = "Hebbal", ["560025"] = "Richmond Town", ["560027"] = "Wilson Garden", ["560030"] = "Adugodi",
            ["560032"] = "RT Nagar", ["560034"] = "Koramangala", ["560035"] = "Carmelaram", ["560036"] = "KR Puram",
            ["560037"] = "Marathahalli", ["560038"] = "Indiranagar", ["560040"] = "Vijayanagar", ["560041"] = "Jayanagar",
            ["560043"] = "Kalyan Nagar", ["560045"] = "Nagawara", ["560046"] = "Benson Town", ["560047"] = "Viveknagar",
            ["560048"] = "Mahadevapura", ["560049"] = "Virgonagar", ["560054"] = "Mathikere", ["560058"] = "Peenya",
            ["560060"] = "Kengeri", ["560061"] = "Subramanyapura", ["560064"] = "Yelahanka", ["560066"] = "Whitefield",
            ["560067"] = "Kadugodi", ["560068"] = "Bommanahalli", ["560070"] = "Banashankari", ["560071"] = "Domlur",
            ["560072"] = "Nagarbhavi", ["560073"] = "Nagasandra", ["560076"] = "Bannerghatta Road", ["560077"] = "Kothanur",
            ["560078"] = "JP Nagar", ["560079"] = "Basaveshwaranagar", ["560080"] = "Sadashivanagar", ["560083"] = "Bannerghatta",
            ["560085"] = "Banashankari 3rd Stage", ["560086"] = "Mahalakshmi Layout", ["560087"] = "Varthur", ["560092"] = "Sahakara Nagar",
            ["560093"] = "CV Raman Nagar", ["560094"] = "RMV 2nd Stage", ["560097"] = "Vidyaranyapura", ["560098"] = "RR Nagar",
            ["560099"] = "Bommasandra", ["560100"] = "Electronic City", ["560102"] = "HSR Layout", ["560103"] = "Bellandur",
            ["562106"] = "Anekal", ["562125"] = "Sarjapura", ["562157"] = "Airport Road", ["562110"] = "Devanahalli",
            ["562123"] = "Nelamangala", ["562149"] = "Bagalur"
        };
        for (var i = 1; i <= 117; i++)
        {
            var pin = $"560{i:000}";
            yield return (pin, names.GetValueOrDefault(pin));
        }
        foreach (var pin in names.Keys.Where(k => k.StartsWith("562")))
            yield return (pin, names[pin]);
    }
}
