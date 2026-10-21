using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace WoodsyCart.Api.Seo;

public class SeoOptions
{
    /// <summary>
    /// The storefront's web folder on this server, e.g. C:\Inetpub\vhosts\woodsycart.com\httpdocs. When set, robots.txt,
    /// sitemap.xml and share/… preview pages are written there (for IIS / Plesk, where the storefront can't pass requests
    /// to the API). Leave empty with nginx, which forwards those requests to <c>api/seo</c> instead.
    /// </summary>
    public string? StorefrontFolder { get; set; }
    public int EveryMinutes { get; set; } = 60;
}

/// <summary>
/// Writes the storefront's search and link-preview files every hour:
/// <c>robots.txt</c>, <c>sitemap.xml</c>, <c>share/index.html</c>, <c>share/product/{slug}.html</c>, <c>share/offers/{slug}.html</c>.
/// The storefront's web.config sends chat-app bots (WhatsApp, Facebook, …) to the share/ pages.
/// </summary>
public partial class SeoFilesWriter(IServiceScopeFactory scopes, IOptions<SeoOptions> options, ILogger<SeoFilesWriter> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var root = options.Value.StorefrontFolder;
        if (string.IsNullOrWhiteSpace(root)) return; // nginx setup: served live by SeoController
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); // let start-up (migrations) finish first
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await WriteAllAsync(root, stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogError(ex, "Writing SEO files to {Folder} failed", root); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, options.Value.EveryMinutes)), stoppingToken);
        }
    }

    private async Task WriteAllAsync(string root, CancellationToken ct)
    {
        if (!Directory.Exists(root)) { logger.LogWarning("Seo:StorefrontFolder {Folder} does not exist — skipping", root); return; }
        using var scope = scopes.CreateScope();
        var seo = scope.ServiceProvider.GetRequiredService<SeoBuilder>();

        await WriteAsync(Path.Combine(root, "robots.txt"), seo.Robots(), ct);
        await WriteAsync(Path.Combine(root, "sitemap.xml"), await seo.SitemapAsync(), ct);
        await WriteAsync(Path.Combine(root, "share", "index.html"), await seo.PreviewAsync("/"), ct);

        var written = 0;
        foreach (var (folder, slugs) in new[] { ("product", await seo.ProductSlugsAsync()), ("offers", await seo.LiveOfferSlugsAsync()) })
        {
            var dir = Path.Combine(root, "share", folder);
            Directory.CreateDirectory(dir);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slug in slugs.Where(s => SafeSlug().IsMatch(s)))
            {
                ct.ThrowIfCancellationRequested();
                var file = Path.Combine(dir, slug + ".html");
                keep.Add(file);
                await WriteAsync(file, await seo.PreviewAsync($"/{folder}/{slug}"), ct);
                written++;
            }
            // Hidden products and finished offers: remove their preview pages
            foreach (var old in Directory.GetFiles(dir, "*.html").Where(f => !keep.Contains(f))) File.Delete(old);
        }
        logger.LogInformation("SEO files written to {Folder} ({Count} preview pages)", root, written);
    }

    /// <summary>Only rewrites a file when its content changed, and swaps it in whole (no half-written files).</summary>
    private static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct) == content) return;
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, new UTF8Encoding(false), ct);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Slugs become file names: letters, digits and dashes only (anything else is skipped).</summary>
    [GeneratedRegex("^[A-Za-z0-9-]{1,200}$")]
    private static partial Regex SafeSlug();
}
