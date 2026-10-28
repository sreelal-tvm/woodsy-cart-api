using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WoodsyCart.Infrastructure.Social;

/// <summary>Every minute: posts what is due. Every 10 minutes: queues new products (if switched on) and checks live posts
/// against their products (Facebook text updated, Instagram and Threads marked out of date), reads views, likes and comments;
/// renews the Threads token weekly.</summary>
public class SocialWorker(IServiceScopeFactory scopes, ILogger<SocialWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }
        var lastCheck = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var social = scope.ServiceProvider.GetRequiredService<SocialService>();
                if (DateTime.UtcNow - lastCheck > TimeSpan.FromMinutes(10))
                {
                    lastCheck = DateTime.UtcNow;
                    await social.AutoQueueAsync(stoppingToken);
                    await social.RefreshThreadsTokenAsync(stoppingToken);
                    // Catalog feed photos: a few products at a time so the first run doesn't load the server
                    await scope.ServiceProvider.GetRequiredService<CatalogFeed>().PrepareImagesAsync(40, stoppingToken);
                    await social.RefreshAsync(ct: stoppingToken);
                    // Views, likes and comments of live posts (each post only when it is due)
                    try { await scope.ServiceProvider.GetRequiredService<SocialEngagement>().RefreshAsync(stoppingToken); }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Reading social media numbers and comments failed"); }
                }
                await social.PublishDueAsync(stoppingToken);
            }
            // Never let a posting problem stop the worker (or, through it, the API).
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Social media posting run failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
