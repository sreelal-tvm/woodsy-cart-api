using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Images;
using WoodsyCart.Infrastructure.Payments;
using WoodsyCart.Infrastructure.Services;
using WoodsyCart.Infrastructure.Vendors;

namespace WoodsyCart.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, string webRootPath)
    {
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(config.GetConnectionString("Default"),
            sql => sql.EnableRetryOnFailure(3).UseQuerySplittingBehavior(QuerySplittingBehavior.SingleQuery)));

        services.Configure<JwtOptions>(config.GetSection("Jwt"));
        services.Configure<RazorpayOptions>(config.GetSection("Razorpay"));
        services.Configure<SmtpOptions>(config.GetSection("Smtp"));

        services.AddSingleton(new FileStorage(webRootPath));
        services.AddScoped<AuthService>();
        services.AddScoped<PricingService>();
        services.AddMemoryCache();
        services.AddScoped<CatalogService>();
        services.AddScoped<OrderService>();
        services.AddScoped<PaymentConfigService>();
        services.AddScoped<PaymentAlertService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<ReportService>();
        services.AddScoped<OfferService>();
        services.AddScoped<ProductOptionsService>();
        services.AddHostedService<OfferPriceWorker>();
        services.AddScoped<EmailSender>();
        services.AddScoped<Email.EmailSettingsService>();
        services.AddSingleton<Email.EmailQueue>();
        services.AddHostedService<Email.EmailWorker>();
        services.AddScoped<DbSeeder>();
        services.AddHttpClient<RazorpayService>();

        services.AddHttpClient<DotShowroomClient>(c =>
        {
            c.BaseAddress = new Uri(DotShowroomClient.BaseUrl);
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; WoodsyCartSync/1.0)");
        });
        services.AddHttpClient("media", c => c.Timeout = TimeSpan.FromSeconds(60));

        // Vendor-mark removal / photo tidy-up (local Python service, see tools/image-cleaner)
        services.Configure<ImageCleanerOptions>(config.GetSection("ImageCleaner"));
        services.AddHttpClient("image-cleaner", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddScoped<ImageCleaningService>();
        services.AddSingleton<ImageCleaningQueue>();
        services.AddHostedService<ImageCleanerHost>();
        services.AddHostedService<ImageCleaningWorker>();
        services.AddScoped<VendorSyncService>();
        services.AddSingleton<SyncProgress>();
        services.AddSingleton<VendorSyncQueue>();
        services.AddHostedService<VendorSyncWorker>();

        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        return services;
    }
}
