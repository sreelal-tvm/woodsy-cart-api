using System.IO.Compression;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;

namespace WoodsyCart.Api.Hosting;

/// <summary>
/// Protects the server when it runs at home behind Cloudflare Tunnel: per-visitor rate limits, a short cache for public
/// catalog reads, and compressed responses. Policy names are used on controller actions.
/// </summary>
public static class Traffic
{
    /// <summary>Sign-in, registration and password resets.</summary>
    public const string Auth = "auth";
    /// <summary>Placing orders.</summary>
    public const string Orders = "orders";
    /// <summary>Enquiries, reviews, returns, cancellations, payment references.</summary>
    public const string Forms = "forms";
    /// <summary>Public order tracking (guessing phone digits must stay slow).</summary>
    public const string Lookup = "lookup";
    /// <summary>Output cache for public catalog reads; evicted whenever Admin changes something.</summary>
    public const string Catalog = "catalog";

    public static IServiceCollection AddTrafficControl(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    new { message = "Too many requests. Please wait a few minutes and try again." }, ct);
            };
            // Every visitor: 300 requests a minute is far above normal browsing. Payment returns and webhooks are never limited.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                ctx.Request.Path.StartsWithSegments("/api/payments")
                    ? RateLimitPartition.GetNoLimiter("payments")
                    : RateLimitPartition.GetSlidingWindowLimiter(ClientKey(ctx), _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = 300, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0
                    }));
            o.AddPolicy(Auth, ctx => Fixed(ctx, 10, TimeSpan.FromMinutes(5)));
            o.AddPolicy(Orders, ctx => Fixed(ctx, 10, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Forms, ctx => Fixed(ctx, 10, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Lookup, ctx => Fixed(ctx, 20, TimeSpan.FromMinutes(10)));
        });

        // Signed-in requests are never cached (default rule), so personal data can't leak between customers.
        services.AddOutputCache(o => o.AddPolicy(Catalog, b => b.Expire(TimeSpan.FromSeconds(60)).Tag(Catalog)));

        services.AddResponseCompression(o =>
        {
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }

    /// <summary>
    /// Behind Cloudflare Tunnel every request arrives from this machine (127.0.0.1); the visitor's address is in
    /// CF-Connecting-IP. The header is trusted only on local connections, so it can't be faked from outside.
    /// </summary>
    public static IApplicationBuilder UseCloudflareClientIp(this IApplicationBuilder app) => app.Use((ctx, next) =>
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is not null && IPAddress.IsLoopback(remote)
            && IPAddress.TryParse(ctx.Request.Headers["CF-Connecting-IP"].ToString(), out var visitor))
            ctx.Connection.RemoteIpAddress = visitor;
        return next();
    });

    /// <summary>Any successful change made in Admin (product, price, banner, offer, review…) clears the catalog cache.</summary>
    public static IApplicationBuilder UseCatalogCacheEviction(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        await next();
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            && ctx.Request.Path.StartsWithSegments("/api/admin") && ctx.Response.StatusCode < 400)
            await ctx.RequestServices.GetRequiredService<IOutputCacheStore>().EvictByTagAsync(Catalog, default);
    });

    private static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static RateLimitPartition<string> Fixed(HttpContext ctx, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits, Window = window, QueueLimit = 0
        });
}
