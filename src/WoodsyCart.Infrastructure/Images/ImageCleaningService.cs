using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WoodsyCart.Domain;
using WoodsyCart.Domain.Entities;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Images;

public class ImageCleanerOptions
{
    /// <summary>Address of the local Python image cleaner (tools/image-cleaner/server.py).</summary>
    public string Url { get; set; } = "http://127.0.0.1:5200";
    /// <summary>Start the cleaner automatically with the API (needs the tool's .venv to exist).</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>Path of tools/image-cleaner, relative to the API content root.</summary>
    public string ToolPath { get; set; } = "../../tools/image-cleaner";
}

public record CleanerText(string Text, double Confidence, string Kind, string Action);

public record CleanerReport(
    List<CleanerText> Texts, string Style, int Removed, int Kept, int Review, string InpaintEngine,
    bool Recentred, bool Enhanced, string Status, string Message);

/// <summary>Talks to the local image cleaner and applies the result to a product photo (keeping the original).</summary>
public class ImageCleaningService(AppDbContext db, FileStorage storage, IHttpClientFactory http, ILogger<ImageCleaningService> logger,
    Microsoft.Extensions.Options.IOptions<ImageCleanerOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private HttpClient Client => http.CreateClient("image-cleaner");

    public async Task<(bool online, string engine)> HealthAsync()
    {
        try
        {
            using var res = await Client.GetAsync($"{options.Value.Url}/health");
            if (!res.IsSuccessStatusCode) return (false, "");
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            return (true, body.TryGetProperty("inpaint", out var e) ? e.GetString() ?? "" : "");
        }
        catch { return (false, ""); }
    }

    /// <summary>Clean one photo. Returns false if the cleaner is offline (the photo stays "not processed").</summary>
    public async Task<bool> ProcessAsync(int imageId, CancellationToken ct = default)
    {
        var img = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (img is null || img.MediaType != MediaType.Image || img.LocalPath is null) return true;
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync(ct);

        // Keep the original exactly as downloaded/uploaded; always clean from the original.
        img.OriginalPath ??= img.LocalPath;
        img.OriginalThumbnailPath ??= img.LocalThumbnailPath;
        var src = storage.FullPath(img.OriginalPath);
        if (!File.Exists(src)) { img.CleanStatus = ImageCleanStatus.Failed; img.CleanReport = "{\"message\":\"original file missing\"}"; await db.SaveChangesAsync(ct); return true; }

        var stem = Path.ChangeExtension(img.OriginalPath, null)!;
        var cleanRel = stem + "_c.webp";
        var thumbRel = stem + "_ct.webp";

        CleanerReport? report;
        try
        {
            using var res = await Client.PostAsJsonAsync($"{options.Value.Url}/process", new
            {
                src, dst = storage.FullPath(cleanRel), thumb = storage.FullPath(thumbRel),
                remove = s.ImageCleanRemoveMarks, recentre = s.ImageCleanRecentre, enhance = s.ImageCleanEnhance, maxSize = 1200
            }, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Image cleaner returned {Status} for image {Id}", res.StatusCode, imageId);
                img.CleanStatus = ImageCleanStatus.Failed;
                img.CleanReport = JsonSerializer.Serialize(new { message = await res.Content.ReadAsStringAsync(ct) });
                await db.SaveChangesAsync(ct);
                return true;
            }
            report = await res.Content.ReadFromJsonAsync<CleanerReport>(Json, ct);
        }
        catch (HttpRequestException)
        {
            return false; // cleaner not running (or it stopped mid-photo) — retried once it is back
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Image cleaner timed out on image {Id}", imageId);
            img.CleanStatus = ImageCleanStatus.Failed;
            img.CleanReport = "{\"message\":\"timed out — use Re-run to try again\"}";
            await db.SaveChangesAsync(ct);
            return true;
        }

        if (report is null) return true;
        var changed = report.Message == "changed" && File.Exists(storage.FullPath(cleanRel));
        img.LocalPath = changed ? cleanRel : img.OriginalPath;
        img.LocalThumbnailPath = changed && File.Exists(storage.FullPath(thumbRel)) ? thumbRel : img.OriginalThumbnailPath;
        img.CleanStatus = report.Status switch
        {
            "cleaned" => ImageCleanStatus.Cleaned,
            "needs_review" => ImageCleanStatus.NeedsReview,
            "failed" => ImageCleanStatus.Failed,
            _ => ImageCleanStatus.Clean
        };
        img.CleanReport = JsonSerializer.Serialize(report, Json);
        if (img.CleanReport.Length > 4000) img.CleanReport = img.CleanReport[..4000];
        img.CleanedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Use the original photo again (cleaned files are kept in case you change your mind).</summary>
    public async Task RestoreAsync(int imageId)
    {
        var img = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId) ?? throw new Common.AppException("Image not found", 404);
        if (img.OriginalPath is null) return;
        img.LocalPath = img.OriginalPath;
        img.LocalThumbnailPath = img.OriginalThumbnailPath;
        img.CleanStatus = ImageCleanStatus.Restored;
        await db.SaveChangesAsync();
    }

    public async Task ApproveAsync(int imageId)
    {
        var img = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId) ?? throw new Common.AppException("Image not found", 404);
        var cleaned = Path.ChangeExtension(img.OriginalPath ?? img.LocalPath ?? "", null) + "_c.webp";
        if (img.OriginalPath is not null && File.Exists(storage.FullPath(cleaned)))
        {
            img.LocalPath = cleaned;
            var thumb = Path.ChangeExtension(img.OriginalPath, null) + "_ct.webp";
            if (File.Exists(storage.FullPath(thumb))) img.LocalThumbnailPath = thumb;
        }
        img.CleanStatus = ImageCleanStatus.Approved;
        await db.SaveChangesAsync();
    }
}

/// <summary>Photos waiting to be cleaned (from vendor sync, uploads and "process all").</summary>
public class ImageCleaningQueue
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

/// <summary>Processes the queue one photo at a time; pauses while the cleaner is offline.</summary>
public class ImageCleaningWorker(ImageCleaningQueue queue, IServiceScopeFactory scopes, ILogger<ImageCleaningWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    using var scope = scopes.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<ImageCleaningService>();
                    if (await svc.ProcessAsync(id, stoppingToken)) break;
                    if (attempt == 0) logger.LogWarning("Image cleaner is offline — waiting before retrying photo {Id}", id);
                    if (attempt >= 20) break; // leave it "not processed"; it can be re-queued from Admin
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                }
            }
            // Never let one photo stop the worker (or, through it, the API).
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Cleaning photo {Id} failed", id); }
            finally { queue.Done(); }
        }
    }
}

/// <summary>Starts tools/image-cleaner/server.py with the API when its virtual environment exists (development convenience),
/// and starts it again if it stops.</summary>
public class ImageCleanerHost(Microsoft.Extensions.Options.IOptions<ImageCleanerOptions> options, IHostEnvironment env,
    ILogger<ImageCleanerHost> logger, IHttpClientFactory http) : BackgroundService
{
    private Process? _process;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.AutoStart) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Our own process is still alive (it may be busy or loading models) — nothing to do.
                if (_process is not { HasExited: false } && !await IsRunningAsync(stoppingToken))
                {
                    if (_process is not null) logger.LogWarning("Image cleaner stopped (exit code {Code}) — restarting it.", _process.ExitCode);
                    if (!Start()) return; // not installed
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Could not start the image cleaner"); }
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    private async Task<bool> IsRunningAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var probe = await http.CreateClient("image-cleaner").GetAsync($"{options.Value.Url}/health", cts.Token);
            return probe.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private bool Start()
    {
        var o = options.Value;
        var tool = Path.GetFullPath(Path.Combine(env.ContentRootPath, o.ToolPath));
        var python = Path.Combine(tool, ".venv", OperatingSystem.IsWindows() ? "Scripts/python.exe" : "bin/python");
        if (!File.Exists(python))
        {
            logger.LogInformation("Image cleaner not installed at {Tool} — photo clean-up is disabled until it is set up.", tool);
            return false;
        }
        _process?.Dispose();
        _process = Process.Start(new ProcessStartInfo(python, "server.py")
        {
            WorkingDirectory = tool, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        });
        if (_process is not null)
        {
            _process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogDebug("[cleaner] {Line}", e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) logger.LogInformation("[cleaner] {Line}", e.Data); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            logger.LogInformation("Image cleaner started (pid {Pid}).", _process.Id);
        }
        return true;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
    }
}
