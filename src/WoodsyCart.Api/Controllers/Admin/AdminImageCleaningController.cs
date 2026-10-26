using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WoodsyCart.Domain;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Data;
using WoodsyCart.Infrastructure.Images;

namespace WoodsyCart.Api.Controllers.Admin;

public record CleaningSettings(bool Auto, bool RemoveMarks, bool Recentre, bool Enhance);
public record RunCleaningRequest(string Scope);   // pending | failed | review | all

public record CleaningItem(int Id, int ProductId, string ProductName, string? OriginalUrl, string? CurrentUrl,
    ImageCleanStatus Status, DateTime? CleanedAt, JsonElement? Report,
    BackgroundStatus Background, string? BackgroundUrl, string? BackgroundModel, string? BackgroundNote, DateTime? BackgroundAt);
public record BackgroundRequest(string Model = "fast");
public record MarkBackgroundRequest(List<int> Ids, bool Marked);

/// <summary>Admin → Image clean-up: remove vendor names/prices from product photos and tidy backgrounds.</summary>
[Route("api/admin/image-cleaning")]
public class AdminImageCleaningController(AppDbContext db, ImageCleaningService cleaner, ImageCleaningQueue queue, BackgroundQueue backgrounds) : AdminControllerBase
{
    [HttpGet("summary")]
    public async Task<object> Summary()
    {
        var (online, engine) = await cleaner.HealthAsync();
        var counts = await db.ProductImages.Where(i => i.MediaType == MediaType.Image && i.LocalPath != null)
            .GroupBy(i => i.CleanStatus).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var bgCounts = await db.ProductImages.Where(i => i.MediaType == MediaType.Image && i.LocalPath != null && i.BackgroundStatus != BackgroundStatus.None)
            .GroupBy(i => i.BackgroundStatus).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var s = await db.BusinessSettings.AsNoTracking().FirstAsync();
        return new
        {
            online, engine, pending = queue.Pending,
            counts = counts.ToDictionary(c => c.Key.ToString(), c => c.Count),
            background = new
            {
                models = online ? await cleaner.BackgroundModelsAsync() : [],
                pending = backgrounds.Pending,
                counts = bgCounts.ToDictionary(c => c.Key.ToString(), c => c.Count)
            },
            settings = new CleaningSettings(s.ImageCleanAuto, s.ImageCleanRemoveMarks, s.ImageCleanRecentre, s.ImageCleanEnhance)
        };
    }

    [HttpGet]
    public async Task<PagedResult<CleaningItem>> List(ImageCleanStatus? status, BackgroundStatus? background, int page = 1, int pageSize = 24, bool changedOnly = false)
    {
        var q = db.ProductImages.AsNoTracking().Where(i => i.MediaType == MediaType.Image && i.LocalPath != null);
        if (status is not null) q = q.Where(i => i.CleanStatus == status);
        if (background is not null) q = q.Where(i => i.BackgroundStatus == background);
        if (changedOnly) q = q.Where(i => i.OriginalPath != null && i.LocalPath != i.OriginalPath);
        var total = await q.CountAsync();
        var rows = await q.OrderByDescending(i => i.CleanStatus == ImageCleanStatus.NeedsReview).ThenByDescending(i => i.CleanedAt).ThenBy(i => i.Id)
            .Skip((Math.Max(1, page) - 1) * pageSize).Take(Math.Clamp(pageSize, 1, 100))
            .Select(i => new
            {
                i.Id, i.ProductId, ProductName = i.Product!.Name, Original = i.OriginalPath ?? i.LocalPath, Current = i.LocalPath, i.CleanStatus, i.CleanedAt, i.CleanReport,
                i.BackgroundStatus, i.BackgroundPath, i.BackgroundModel, i.BackgroundNote, i.BackgroundAt
            })
            .ToListAsync();
        var items = rows.Select(r => new CleaningItem(r.Id, r.ProductId, r.ProductName, r.Original, r.Current, r.CleanStatus, r.CleanedAt,
            string.IsNullOrEmpty(r.CleanReport) ? null : Parse(r.CleanReport),
            r.BackgroundStatus, r.BackgroundPath, r.BackgroundModel, r.BackgroundNote, r.BackgroundAt)).ToList();
        return new PagedResult<CleaningItem> { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    private static JsonElement? Parse(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); } catch { return null; }
    }

    [HttpPost("run")]
    public async Task<object> Run(RunCleaningRequest req)
    {
        var q = db.ProductImages.Where(i => i.MediaType == MediaType.Image && i.LocalPath != null);
        q = req.Scope switch
        {
            "all" => q.Where(i => i.CleanStatus != ImageCleanStatus.Restored && i.CleanStatus != ImageCleanStatus.Approved),
            "failed" => q.Where(i => i.CleanStatus == ImageCleanStatus.Failed),
            "review" => q.Where(i => i.CleanStatus == ImageCleanStatus.NeedsReview),
            _ => q.Where(i => i.CleanStatus == ImageCleanStatus.NotProcessed)
        };
        var ids = await q.OrderBy(i => i.ProductId).Select(i => i.Id).ToListAsync();
        queue.Enqueue(ids);
        return new { queued = ids.Count };
    }

    [HttpPost("{id:int}/approve")]
    public async Task<IActionResult> Approve(int id) { await cleaner.ApproveAsync(id); return NoContent(); }

    [HttpPost("{id:int}/restore")]
    public async Task<IActionResult> Restore(int id) { await cleaner.RestoreAsync(id); return NoContent(); }

    [HttpPost("{id:int}/reprocess")]
    public async Task<IActionResult> Reprocess(int id)
    {
        if (!await db.ProductImages.AnyAsync(i => i.Id == id)) throw new AppException("Image not found", 404);
        if (!await cleaner.ProcessAsync(id)) throw new AppException("The image cleaner isn't running. Start it and try again.", 503);
        return NoContent();
    }

    // ---------- White background ----------

    /// <summary>Make a white-background version of one photo (fast = IS-Net, detailed = BiRefNet).</summary>
    [HttpPost("{id:int}/background")]
    public async Task<object> Background(int id, BackgroundRequest req)
    {
        var ids = await cleaner.QueueBackgroundAsync([id], req.Model);
        if (ids.Count == 0) throw new AppException("This photo is already in the queue.");
        backgrounds.Enqueue(ids);
        return new { queued = ids.Count };
    }

    [HttpPost("{id:int}/background/apply")]
    public async Task<IActionResult> ApplyBackground(int id) { await cleaner.ApplyBackgroundAsync(id); return NoContent(); }

    [HttpPost("{id:int}/background/discard")]
    public async Task<IActionResult> DiscardBackground(int id) { await cleaner.DiscardBackgroundAsync(id); return NoContent(); }

    /// <summary>Mark photos with a showroom or factory background for the batch run (or unmark them).</summary>
    [HttpPost("background/mark")]
    public async Task<object> MarkBackground(MarkBackgroundRequest req) => new { changed = await cleaner.MarkBackgroundAsync(req.Ids, req.Marked) };

    /// <summary>Batch: white background for every marked photo, with the fast model. Results wait for review.</summary>
    [HttpPost("background/run")]
    public async Task<object> RunBackground()
    {
        var marked = await db.ProductImages.Where(i => i.BackgroundStatus == BackgroundStatus.Marked).OrderBy(i => i.ProductId).Select(i => i.Id).ToListAsync();
        var ids = await cleaner.QueueBackgroundAsync(marked, "fast");
        backgrounds.Enqueue(ids);
        return new { queued = ids.Count };
    }

    [HttpPut("settings")]
    public async Task<IActionResult> Settings(CleaningSettings s)
    {
        await db.BusinessSettings.ExecuteUpdateAsync(x => x
            .SetProperty(b => b.ImageCleanAuto, s.Auto).SetProperty(b => b.ImageCleanRemoveMarks, s.RemoveMarks)
            .SetProperty(b => b.ImageCleanRecentre, s.Recentre).SetProperty(b => b.ImageCleanEnhance, s.Enhance));
        return NoContent();
    }
}
