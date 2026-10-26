using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;

namespace WoodsyCart.Infrastructure.Images;

public record BackgroundReport(string Status, string Model, double Seconds, double Coverage, string Message);

/// <summary>
/// White-background replacement: the image cleaner cuts the product out and puts it on white. The result is a candidate
/// (<see cref="BackgroundStatus.Ready"/>) until Admin applies it; the photo shown before is never deleted.
/// </summary>
public partial class ImageCleaningService
{
    public static readonly string[] BackgroundModels = ["fast", "detailed"];

    /// <summary>Which background-removal models the cleaner has installed (empty when it is offline).</summary>
    public async Task<Dictionary<string, bool>> BackgroundModelsAsync()
    {
        try
        {
            using var res = await Client.GetAsync($"{options.Value.Url}/health");
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            return body.TryGetProperty("background", out var b)
                ? b.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean())
                : [];
        }
        catch { return []; }
    }

    /// <summary>Make a white-background version of one photo. Returns false if the cleaner is offline.</summary>
    public async Task<bool> ProcessBackgroundAsync(int imageId, CancellationToken ct = default)
    {
        var img = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (img is null || img.MediaType != MediaType.Image || img.LocalPath is null) return true;
        var model = BackgroundModels.Contains(img.BackgroundModel) ? img.BackgroundModel! : "fast";

        img.OriginalPath ??= img.LocalPath;
        img.OriginalThumbnailPath ??= img.LocalThumbnailPath;
        // Cut out from the photo as the store shows it without a new background (vendor marks already removed).
        var (srcRel, srcThumb) = TextVersion(img);
        if (img.BackgroundStatus == BackgroundStatus.Applied) { img.LocalPath = srcRel; img.LocalThumbnailPath = srcThumb; }
        var src = storage.FullPath(srcRel);
        if (!File.Exists(src)) return await FailBackground(img, "photo file missing", ct);

        var stem = Path.ChangeExtension(img.OriginalPath, null)!;
        var version = DateTime.UtcNow.Ticks.ToString("x");
        var mainRel = $"{stem}_bg-{version}.webp";
        var thumbRel = $"{stem}_bgt-{version}.webp";

        BackgroundReport? report;
        try
        {
            using var res = await Client.PostAsJsonAsync($"{options.Value.Url}/background",
                new { src, dst = storage.FullPath(mainRel), thumb = storage.FullPath(thumbRel), model, size = 1200 }, ct);
            if (!res.IsSuccessStatusCode) return await FailBackground(img, await res.Content.ReadAsStringAsync(ct), ct);
            report = await res.Content.ReadFromJsonAsync<BackgroundReport>(Json, ct);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return await FailBackground(img, "timed out — try again", ct); }

        if (report is null || report.Status != "done" || !File.Exists(storage.FullPath(mainRel)))
            return await FailBackground(img, report?.Message is { Length: > 0 } m ? m : "the cleaner could not make the photo", ct);

        DeleteBackgroundFiles(stem, mainRel, thumbRel);
        img.BackgroundPath = mainRel;
        img.BackgroundThumbnailPath = File.Exists(storage.FullPath(thumbRel)) ? thumbRel : null;
        img.BackgroundStatus = BackgroundStatus.Ready;
        img.BackgroundModel = model;
        img.BackgroundNote = null;
        img.BackgroundAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Show the white-background version on the store.</summary>
    public async Task ApplyBackgroundAsync(int imageId)
    {
        var img = await FindImage(imageId);
        if (img.BackgroundPath is null || !File.Exists(storage.FullPath(img.BackgroundPath)))
            throw new Common.AppException("There is no white-background version of this photo yet.");
        img.LocalPath = img.BackgroundPath;
        img.LocalThumbnailPath = img.BackgroundThumbnailPath ?? img.LocalThumbnailPath;
        img.BackgroundStatus = BackgroundStatus.Applied;
        await db.SaveChangesAsync();
    }

    /// <summary>Keep the photo as it was: the store goes back to the previous version and the candidate is deleted.</summary>
    public async Task DiscardBackgroundAsync(int imageId)
    {
        var img = await FindImage(imageId);
        if (img.BackgroundStatus == BackgroundStatus.Applied)
            (img.LocalPath, img.LocalThumbnailPath) = TextVersion(img);
        if (img.OriginalPath is not null) DeleteBackgroundFiles(Path.ChangeExtension(img.OriginalPath, null)!, null, null);
        img.BackgroundPath = img.BackgroundThumbnailPath = null;
        img.BackgroundStatus = BackgroundStatus.Discarded;
        await db.SaveChangesAsync();
    }

    /// <summary>Mark photos (showroom or factory backgrounds) for the next batch run, or unmark them.</summary>
    public async Task<int> MarkBackgroundAsync(IReadOnlyCollection<int> imageIds, bool marked)
    {
        var from = marked
            ? new[] { BackgroundStatus.None, BackgroundStatus.Discarded, BackgroundStatus.Failed }
            : new[] { BackgroundStatus.Marked };
        return await db.ProductImages.Where(i => imageIds.Contains(i.Id) && from.Contains(i.BackgroundStatus))
            .ExecuteUpdateAsync(x => x.SetProperty(i => i.BackgroundStatus, marked ? BackgroundStatus.Marked : BackgroundStatus.None));
    }

    /// <summary>Put photos in the background queue with the chosen model.</summary>
    public async Task<List<int>> QueueBackgroundAsync(IReadOnlyCollection<int> imageIds, string model)
    {
        if (!BackgroundModels.Contains(model)) throw new Common.AppException($"Unknown cut-out model '{model}'.");
        var ids = await db.ProductImages.Where(i => imageIds.Contains(i.Id) && i.MediaType == MediaType.Image && i.LocalPath != null
                && i.BackgroundStatus != BackgroundStatus.Queued)
            .Select(i => i.Id).ToListAsync();
        await db.ProductImages.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(x => x
            .SetProperty(i => i.BackgroundStatus, BackgroundStatus.Queued).SetProperty(i => i.BackgroundModel, model)
            .SetProperty(i => i.BackgroundNote, (string?)null));
        return ids;
    }

    /// <summary>The photo the store shows when no white background is applied: the vendor-mark clean-up result or the original.</summary>
    private (string Main, string? Thumb) TextVersion(ProductImage img)
    {
        if (img.BackgroundStatus != BackgroundStatus.Applied) return (img.LocalPath!, img.LocalThumbnailPath);
        var original = (img.OriginalPath ?? img.LocalPath!, img.OriginalThumbnailPath ?? img.LocalThumbnailPath);
        if (img.OriginalPath is null || img.CleanStatus == ImageCleanStatus.Restored) return original;
        return LatestCleaned(Path.ChangeExtension(img.OriginalPath, null)!) ?? original;
    }

    private void DeleteBackgroundFiles(string stem, string? keepMain, string? keepThumb)
    {
        var (dir, name, _) = Locate(stem);
        if (!Directory.Exists(dir)) return;
        var keep = new[] { keepMain, keepThumb }.Where(k => k is not null).Select(k => Path.GetFileName(k!)).ToArray();
        foreach (var pattern in new[] { $"{name}_bg-*.webp", $"{name}_bgt-*.webp" })
            foreach (var f in Directory.GetFiles(dir, pattern))
                if (!keep.Contains(Path.GetFileName(f)))
                    try { File.Delete(f); } catch (IOException) { /* in use; removed next time */ }
    }

    private async Task<bool> FailBackground(ProductImage img, string why, CancellationToken ct)
    {
        img.BackgroundStatus = BackgroundStatus.Failed;
        img.BackgroundNote = why.Length > 500 ? why[..500] : why;
        img.BackgroundAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<ProductImage> FindImage(int id) =>
        await db.ProductImages.FirstOrDefaultAsync(i => i.Id == id) ?? throw new Common.AppException("Image not found", 404);
}

/// <summary>Photos waiting for a white background (single photos and batch runs from Admin).</summary>
public class BackgroundQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    private int _pending;
    public int Pending => _pending;

    public void Enqueue(IEnumerable<int> imageIds)
    {
        foreach (var id in imageIds)
            if (_channel.Writer.TryWrite(id)) Interlocked.Increment(ref _pending);
    }

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
    public void Done() => Interlocked.Decrement(ref _pending);
}

/// <summary>Makes white-background photos one at a time; picks up photos left queued when the API last stopped.</summary>
public class BackgroundWorker(BackgroundQueue queue, IServiceScopeFactory scopes, ILogger<BackgroundWorker> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            queue.Enqueue(await db.ProductImages.Where(i => i.BackgroundStatus == BackgroundStatus.Queued).OrderBy(i => i.Id)
                .Select(i => i.Id).ToListAsync(stoppingToken));
        }
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    using var scope = scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ImageCleaningService>();
                    if (await svc.ProcessBackgroundAsync(id, stoppingToken)) break;
                    if (attempt == 0) logger.LogWarning("Image cleaner is offline — waiting before retrying background for photo {Id}", id);
                    if (attempt >= 20) break; // stays "queued"; picked up again at the next start
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "White background for photo {Id} failed", id); }
            finally { queue.Done(); }
        }
    }
}
