using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using WoodsyCart.Domain.Common;
using WoodsyCart.Domain.Entities;

namespace WoodsyCart.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductOption> ProductOptions => Set<ProductOption>();
    public DbSet<ProductOptionValue> ProductOptionValues => Set<ProductOptionValue>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Banner> Banners => Set<Banner>();
    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<WishlistItem> WishlistItems => Set<WishlistItem>();
    public DbSet<Enquiry> Enquiries => Set<Enquiry>();
    public DbSet<ServiceablePincode> ServiceablePincodes => Set<ServiceablePincode>();

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<Coupon> Coupons => Set<Coupon>();

    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<VendorBill> VendorBills => Set<VendorBill>();
    public DbSet<CustomerInvoice> CustomerInvoices => Set<CustomerInvoice>();
    public DbSet<CustomerInvoiceLine> CustomerInvoiceLines => Set<CustomerInvoiceLine>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<VendorSyncLog> VendorSyncLogs => Set<VendorSyncLog>();
    public DbSet<BusinessSettings> BusinessSettings => Set<BusinessSettings>();
    public DbSet<PaymentProviderConfig> PaymentProviderConfigs => Set<PaymentProviderConfig>();
    public DbSet<PaymentMethodOption> PaymentMethodOptions => Set<PaymentMethodOption>();
    public DbSet<PaymentAlert> PaymentAlerts => Set<PaymentAlert>();
    public DbSet<EmailConfig> EmailConfigs => Set<EmailConfig>();
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<decimal>().HavePrecision(18, 2);
        builder.Properties<decimal?>().HavePrecision(18, 2);
        builder.Properties<string>().HaveMaxLength(500);
        // All timestamps are stored in UTC; mark them as UTC on read so JSON carries the "Z" suffix.
        builder.Properties<DateTime>().HaveConversion<UtcConverter>();
        builder.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    private class UtcConverter() : ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    private class NullableUtcConverter() : ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Category>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Description).HasMaxLength(4000);
        });
        b.Entity<Collection>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<EmailConfig>(e => { e.Property(x => x.PasswordProtected).HasMaxLength(2000); e.Property(x => x.AdminEmails).HasMaxLength(1000); e.Property(x => x.EventsJson).HasMaxLength(4000); });
        b.Entity<EmailLog>(e => e.HasIndex(x => x.CreatedAt));

        b.Entity<Product>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.VendorProductId);
            e.HasIndex(x => new { x.IsActive, x.IsDeleted, x.CategoryId });
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Description).HasMaxLength(int.MaxValue);
            e.Property(x => x.DescriptionHtml).HasMaxLength(int.MaxValue);
            e.Property(x => x.VendorUrl).HasMaxLength(1000);
            e.HasOne(x => x.Category).WithMany(c => c.Products).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Collections).WithMany(c => c.Products).UsingEntity(j => j.ToTable("ProductCollections"));
            e.HasMany(x => x.Tags).WithMany(t => t.Products).UsingEntity(j => j.ToTable("ProductTags"));
            e.HasQueryFilter(x => !x.IsDeleted);
            // NoAction: SQL Server rejects a second cascade path to OfferProducts; deleting an offer clears this first.
            e.HasOne(x => x.Offer).WithMany().HasForeignKey(x => x.OfferId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(x => x.OfferId);
        });
        b.Entity<Offer>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Description).HasMaxLength(4000);
            e.Property(x => x.ThemeColor).HasMaxLength(20);
            e.HasMany(x => x.Categories).WithMany().UsingEntity<Dictionary<string, object>>("OfferCategories",
                r => r.HasOne<Category>().WithMany().HasForeignKey("CategoryId").OnDelete(DeleteBehavior.Cascade),
                l => l.HasOne<Offer>().WithMany().HasForeignKey("OfferId").OnDelete(DeleteBehavior.Cascade));
            e.HasMany(x => x.Products).WithMany().UsingEntity<Dictionary<string, object>>("OfferProducts",
                r => r.HasOne<Product>().WithMany().HasForeignKey("ProductId").OnDelete(DeleteBehavior.Cascade),
                l => l.HasOne<Offer>().WithMany().HasForeignKey("OfferId").OnDelete(DeleteBehavior.Cascade));
        });
        b.Entity<ProductImage>(e =>
        {
            e.Property(x => x.Url).HasMaxLength(1000);
            e.Property(x => x.ThumbnailUrl).HasMaxLength(1000);
            e.Property(x => x.CleanReport).HasMaxLength(4000);
            e.HasIndex(x => x.CleanStatus);
            e.HasOne(x => x.Product).WithMany(p => p.Images).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductOption>(e =>
        {
            e.HasOne(x => x.Product).WithMany(p => p.Options).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductOptionValue>(e =>
        {
            e.HasOne(x => x.Option).WithMany(o => o.Values).HasForeignKey(x => x.ProductOptionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ProductVariant>(e =>
        {
            e.HasOne(x => x.Product).WithMany(p => p.Variants).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.OptionValues).WithMany(v => v.Variants)
                .UsingEntity<Dictionary<string, object>>("ProductVariantOptionValues",
                    r => r.HasOne<ProductOptionValue>().WithMany().HasForeignKey("ProductOptionValueId").OnDelete(DeleteBehavior.Cascade),
                    l => l.HasOne<ProductVariant>().WithMany().HasForeignKey("ProductVariantId").OnDelete(DeleteBehavior.NoAction));
        });
        b.Entity<Review>(e =>
        {
            e.Property(x => x.Comment).HasMaxLength(4000);
            e.HasOne(x => x.Product).WithMany(p => p.Reviews).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<AppUser>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.Phone);
        });
        b.Entity<Address>().HasOne(x => x.User).WithMany(u => u.Addresses).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CartItem>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Variant).WithMany().HasForeignKey(x => x.VariantId).OnDelete(DeleteBehavior.NoAction);
        });
        b.Entity<WishlistItem>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ProductId }).IsUnique();
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Enquiry>(e =>
        {
            e.Property(x => x.Message).HasMaxLength(4000);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<ServiceablePincode>().HasIndex(x => x.Pincode).IsUnique();

        b.Entity<Order>(e =>
        {
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.Property(x => x.AccessToken).HasMaxLength(64);
            e.Property(x => x.PaymentProvider).HasMaxLength(40);
            e.Property(x => x.CustomerUtr).HasMaxLength(40);
            e.HasIndex(x => new { x.PaymentStatus, x.UpiAmount });
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.CustomerNotes).HasMaxLength(2000);
            e.Property(x => x.AdminNotes).HasMaxLength(4000);
            e.HasOne(x => x.User).WithMany(u => u.Orders).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.History).WithOne().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<OrderItem>(e =>
        {
            e.Property(x => x.VendorUrl).HasMaxLength(1000);
            e.Property(x => x.ImageUrl).HasMaxLength(1000);
            e.HasOne(x => x.Order).WithMany(o => o.Items).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<Payment>().HasOne(x => x.Order).WithMany(o => o.Payments).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Refund>().HasOne(x => x.Order).WithMany(o => o.Refunds).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Coupon>().HasIndex(x => x.Code).IsUnique();

        b.Entity<PurchaseOrder>(e =>
        {
            e.HasIndex(x => x.PoNumber).IsUnique();
            e.HasOne(x => x.Order).WithOne(o => o.PurchaseOrder).HasForeignKey<PurchaseOrder>(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<VendorBill>(e =>
        {
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<CustomerInvoice>(e =>
        {
            e.HasIndex(x => x.InvoiceNumber).IsUnique();
            e.HasIndex(x => new { x.FinancialYear, x.Sequence }).IsUnique();
            e.HasOne(x => x.Order).WithMany(o => o.Invoices).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.CustomerInvoiceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Expense>().HasIndex(x => x.Date);
        b.Entity<VendorSyncLog>().Property(x => x.Message).HasMaxLength(4000);
        b.Entity<PaymentProviderConfig>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(40);
            e.Property(x => x.SettingsJson).HasMaxLength(4000);
            e.Property(x => x.ProtectedSecrets).HasMaxLength(4000);
        });
        b.Entity<PaymentMethodOption>(e =>
        {
            e.HasIndex(x => x.Method).IsUnique();
            e.Property(x => x.ProviderCode).HasMaxLength(40);
        });
        b.Entity<PaymentAlert>(e =>
        {
            e.Property(x => x.RawText).HasMaxLength(2000);
            e.HasIndex(x => x.Utr);
            e.HasIndex(x => new { x.Status, x.Amount });
            e.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<BusinessSettings>(e =>
        {
            e.Property(x => x.InvoiceTerms).HasMaxLength(4000);
            e.Property(x => x.BankDetails).HasMaxLength(2000);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        return base.SaveChangesAsync(ct);
    }
}
