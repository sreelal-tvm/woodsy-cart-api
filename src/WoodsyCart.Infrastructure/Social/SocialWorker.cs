using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WoodsyCart.Infrastructure.Social;

/// <summary>Every minute: posts what is due. Every 10 minutes: queues new products (if switched on) and checks live posts
/// against their products (Facebook text updated, Instagram marked out of date).</summary>
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
                    await social.RefreshAsync(ct: stoppingToken);
                }
                await social.PublishDueAsync(stoppingToken);
            }
            // Never let a posting problem stop the worker (or, through it, the API).
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Social media posting run failed"); }
            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { }
        }
    }
}
