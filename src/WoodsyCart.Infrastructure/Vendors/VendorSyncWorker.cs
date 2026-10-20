using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Infrastructure.Data;

namespace WoodsyCart.Infrastructure.Vendors;

public record SyncRequest(int VendorId, SyncOptions Options);

public class VendorSyncQueue
{
    private readonly Channel<SyncRequest> _channel = Channel.CreateBounded<SyncRequest>(new BoundedChannelOptions(5) { FullMode = BoundedChannelFullMode.DropWrite });
    public bool Enqueue(SyncRequest request) => _channel.Writer.TryWrite(request);
    public IAsyncEnumerable<SyncRequest> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public class VendorSyncWorker(
    VendorSyncQueue queue,
    SyncProgress progress,
    IServiceScopeFactory scopes,
    IConfiguration config,
    IMemoryCache cache,
    ILogger<VendorSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _ = AutoSyncLoopAsync(stoppingToken);
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            progress.IsRunning = true;
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<VendorSyncService>();
                await service.RunAsync(request.VendorId, request.Options, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Vendor sync crashed");
                progress.LastError = ex.Message;
            }
            finally
            {
                progress.IsRunning = false;
                progress.Stage = "Idle";
                cache.Remove(Services.CatalogService.CategoriesCacheKey);
            }
        }
    }

    /// <summary>Optional scheduled sync: Vendor:AutoSyncHours (0 disables).</summary>
    private async Task AutoSyncLoopAsync(CancellationToken ct)
    {
        var hours = config.GetValue<int>("Vendor:AutoSyncHours");
        if (hours <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (progress.IsRunning) continue;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var vendors = await db.Vendors.Where(v => v.SyncEnabled && v.ExternalStoreId != null).Select(v => v.Id).ToListAsync(ct);
            foreach (var id in vendors) queue.Enqueue(new SyncRequest(id, new SyncOptions { PublishNewProducts = false }));
        }
    }
}
