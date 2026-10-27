using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using WoodsyCart.Infrastructure.Common;
using WoodsyCart.Infrastructure.Services;

namespace WoodsyCart.Infrastructure.Social;

/// <summary>What a reel says: the name and price on the first photo, one or two facts on each next photo, then an end card.</summary>
public record ReelScript(string Name, string? Price, string? Mrp, string? Saving, string? Offer, List<string> Facts,
    string Website, string? WhatsApp, string Code, string? Tagline);

public record ReelFile(string Video, string Cover, string? Track);

/// <summary>
/// Turns a product's photos into a 9:16 reel (1080×1920, about 10–15 seconds): each photo slowly zooms with its text,
/// fades into the next, and an end card says where to order. Slides are drawn with SkiaSharp, the video is made by FFmpeg
/// (C:\WoodsyCart\tools\ffmpeg). Music is a royalty-free track uploaded in Admin, or none.
/// </summary>
public class ReelMaker(FileStorage storage, IConfiguration config, ILogger<ReelMaker> logger)
{
    public const int W = 1080, H = 1920, Fps = 30, MaxPhotos = 5;
    public const string MusicFolder = "/media/social/music";
    public static readonly string[] MusicTypes = [".mp3", ".m4a", ".aac", ".wav"];
    private const double SlideSeconds = 2.8, EndSeconds = 2.6, FadeSeconds = 0.4;
    // Safe zones (Instagram is the strictest; Facebook and Threads cover less): the app's own title and buttons cover the
    // top ~150 px, the account name and caption the bottom ~400 px, and the like/comment buttons the right ~120 px.
    // So everything sits between y 160 and 1520, and text stays left of x 940.
    private const int BoxX = 40, BoxY = 275, BoxW = 1000, BoxH = 950, TextTop = 1250, TextWidth = 880;
    private static readonly SemaphoreSlim Gate = new(1, 1);   // one video at a time: FFmpeg uses every core
    private static readonly SKColor Brand = SKColor.Parse("#9A5226"), Dark = SKColor.Parse("#2A211C"),
        Cream = SKColor.Parse("#F3EEE7"), Grey = SKColor.Parse("#7A6E66");

    public string FfmpegPath => config["Social:FfmpegPath"] ?? @"C:\WoodsyCart\tools\ffmpeg\ffmpeg.exe";
    public bool Available => File.Exists(FfmpegPath);

    public List<string> MusicTracks()
    {
        var dir = storage.FullPath(MusicFolder);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir).Where(f => MusicTypes.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase).ToList()!;
    }

    /// <summary>Makes the reel (or returns it if this product's text, photos and track haven't changed).
    /// Each product always gets the same track, so remaking it doesn't change its music.</summary>
    public async Task<ReelFile> MakeAsync(int productId, ReelScript s, IReadOnlyList<string> photos, bool withMusic, CancellationToken ct = default, bool remake = false)
    {
        if (!Available) throw new AppException($"Reels need FFmpeg at {FfmpegPath} (see the handbook, section 15).");
        if (photos.Count == 0) throw new AppException("The product has no photos.");
        var tracks = MusicTracks();
        var track = withMusic && tracks.Count > 0 ? tracks[productId % tracks.Count] : null;
        var folder = $"/media/social/{productId}";
        string Key(string? t) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(s) + "|" + string.Join("|", photos.Take(MaxPhotos)) + "|" + t + "|v4")))[..16].ToLowerInvariant();
        var key = Key(track);
        var result = new ReelFile($"{folder}/reel-{key}.mp4", $"{folder}/reel-{key}.jpg", track);
        if (remake) foreach (var f in new[] { result.Video, result.Cover }) if (File.Exists(storage.FullPath(f))) File.Delete(storage.FullPath(f));
        if (File.Exists(storage.FullPath(result.Video)) && File.Exists(storage.FullPath(result.Cover))) return result;

        await Gate.WaitAsync(ct);
        var temp = Path.Combine(Path.GetTempPath(), "woodsycart-reel-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (File.Exists(storage.FullPath(result.Video)) && File.Exists(storage.FullPath(result.Cover))) return result;
            Directory.CreateDirectory(temp);
            Directory.CreateDirectory(storage.FullPath(folder));
            var slides = Render(s, photos.Take(MaxPhotos).Select(storage.FullPath).ToList(), temp, storage.FullPath(result.Cover));
            var output = Path.Combine(temp, "reel.mp4");
            await RunFfmpegAsync(Arguments(slides, track is null ? null : storage.FullPath($"{MusicFolder}/{track}"), output), ct);
            File.Move(output, storage.FullPath(result.Video), true);

            // Keep only this product's current reels (with and without music)
            var keep = new[] { key, Key(null) }.ToHashSet();
            foreach (var f in Directory.GetFiles(storage.FullPath(folder), "reel-*"))
                if (!keep.Contains(Path.GetFileNameWithoutExtension(f)[5..])) try { File.Delete(f); } catch (IOException) { }
            logger.LogInformation("Made the reel for product {Product} ({Slides} slides, music {Track})", productId, slides.Count, track ?? "none");
            return result;
        }
        finally
        {
            Gate.Release();
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------- Slides ----------

    private record Slide(string Base, string? Photo, double Seconds);

    /// <summary>Draws each slide's background and text (and the photo, 2× size for a smooth zoom) as PNGs; the first
    /// slide with its photo becomes the cover.</summary>
    private static List<Slide> Render(ReelScript s, List<string> photos, string temp, string coverPath)
    {
        var slides = new List<Slide>();
        // Facts over the photos after the first, three per slide at most; a single photo carries two under the price.
        var factSlides = Math.Max(1, photos.Count - 1);
        var perSlide = photos.Count == 1 ? 2 : Math.Min(3, (int)Math.Ceiling(s.Facts.Count / (double)factSlides));
        for (var i = 0; i < photos.Count; i++)
        {
            var facts = photos.Count == 1 ? s.Facts.Take(2).ToList()
                : i == 0 ? [] : s.Facts.Skip((i - 1) * Math.Max(1, perSlide)).Take(Math.Max(1, perSlide)).ToList();
            var basePath = Path.Combine(temp, $"base{i}.png");
            var photoPath = Path.Combine(temp, $"photo{i}.png");
            using (var bmp = new SKBitmap(W, H))
            using (var canvas = new SKCanvas(bmp))
            {
                Background(canvas, s);
                if (i == 0) Headline(canvas, s, facts); else Facts(canvas, s, facts);
                Save(bmp, basePath);
                using var photo = FitPhoto(photos[i], 2);
                Save(photo, photoPath);
                if (i == 0)
                {
                    using var small = FitPhoto(photos[i], 1);
                    canvas.DrawBitmap(small, BoxX, BoxY);
                    using var img = SKImage.FromBitmap(bmp);
                    using var jpg = img.Encode(SKEncodedImageFormat.Jpeg, 90);
                    using var fs = File.Create(coverPath);
                    jpg.SaveTo(fs);
                }
            }
            slides.Add(new Slide(basePath, photoPath, photos.Count == 1 ? 4.5 : SlideSeconds));
        }
        var endPath = Path.Combine(temp, "end.png");
        using (var bmp = new SKBitmap(W, H))
        using (var canvas = new SKCanvas(bmp))
        {
            EndCard(canvas, s);
            Save(bmp, endPath);
        }
        slides.Add(new Slide(endPath, null, EndSeconds));
        return slides;
    }

    private static void Background(SKCanvas c, ReelScript s)
    {
        c.Clear(Cream);
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        c.DrawRect(BoxX, BoxY, BoxW, BoxH, white);
        Text(c, "WoodsyCart", 60, 205, 52, Brand, bold: true);
        if (s.Tagline is { Length: > 0 }) Text(c, s.Tagline, 60, 248, 28, Grey, maxWidth: TextWidth);
    }

    private static void Headline(SKCanvas c, ReelScript s, List<string> facts)
    {
        var y = (float)TextTop;
        if (s.Offer is { Length: > 0 }) { Pill(c, s.Offer.ToUpperInvariant(), 60, y, 28, Brand, SKColors.White); y += 56; }
        y = Wrapped(c, s.Name, 60, y + 52, 52, Dark, TextWidth, 2, bold: true) + 10;
        if (s.Price is { Length: > 0 })
        {
            var x = Text(c, s.Price, 60, y + 76, 76, Brand, bold: true) + 22;
            if (s.Mrp is { Length: > 0 }) x = Text(c, "MRP " + s.Mrp, x, y + 76, 34, Grey, strike: true) + 18;
            if (s.Saving is { Length: > 0 } && x + 220 < 60 + TextWidth) Pill(c, "Save " + s.Saving, x, y + 30, 30, SKColor.Parse("#25b003"), SKColors.White);
            y += 90;
        }
        foreach (var f in facts) y = Wrapped(c, "• " + f, 60, y + 46, 36, Dark, TextWidth, 1) + 4;
    }

    private static void Facts(SKCanvas c, ReelScript s, List<string> facts)
    {
        if (facts.Count == 0) { Wrapped(c, s.Name, 60, TextTop + 70, 52, Dark, TextWidth, 2, bold: true); return; }
        var y = (float)TextTop;
        var size = facts.Count >= 3 ? 40f : 50f;   // three facts must still end above the caption area (y 1520)
        using var dot = new SKPaint { Color = Brand, IsAntialias = true };
        foreach (var f in facts)
        {
            c.DrawCircle(78, y + size * 0.78f, 11, dot);
            y = Wrapped(c, f, 110, y + size * 1.1f, size, Dark, TextWidth - 50, 2, bold: true) + size * 0.55f;
        }
    }

    private static void EndCard(SKCanvas c, ReelScript s)
    {
        c.Clear(Brand);
        Centered(c, "WoodsyCart", 520, 92, SKColors.White, bold: true);
        if (s.Tagline is { Length: > 0 }) Centered(c, s.Tagline, 590, 34, Cream);
        Centered(c, "Order now", 800, 78, SKColors.White, bold: true);
        Centered(c, s.Website, 895, 58, SKColors.White);
        Centered(c, $"Search {s.Code} on our website", 985, 38, Cream);
        if (s.WhatsApp is { Length: > 0 }) Centered(c, "WhatsApp " + s.WhatsApp, 1100, 50, SKColors.White, bold: true);
        Centered(c, "Delivered & assembled at your home", 1210, 38, Cream);
    }

    /// <summary>The photo, whole (never cropped), centred on white in the photo box.</summary>
    private static SKBitmap FitPhoto(string path, int scale)
    {
        using var src = SKBitmap.Decode(path) ?? throw new IOException("Can't read photo " + Path.GetFileName(path));
        var w = BoxW * scale; var h = BoxH * scale;
        var bmp = new SKBitmap(w, h);
        using var c = new SKCanvas(bmp);
        c.Clear(SKColors.White);
        var k = Math.Min((float)w / src.Width, (float)h / src.Height) * 0.94f;
        var dw = src.Width * k; var dh = src.Height * k;
        using var img = SKImage.FromBitmap(src);
        c.DrawImage(img, SKRect.Create((w - dw) / 2, (h - dh) / 2, dw, dh), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return bmp;
    }

    // ---------- Text helpers ----------

    private static SKFont Font(float size, bool bold) => new(SKTypeface.FromFamilyName("Segoe UI",
        bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) ?? SKTypeface.Default, size);

    /// <summary>Draws one line; returns the x where it ends.</summary>
    private static float Text(SKCanvas c, string text, float x, float y, float size, SKColor color, bool bold = false, bool strike = false, float maxWidth = 0)
    {
        using var font = Font(size, bold);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        if (maxWidth > 0) while (text.Length > 1 && font.MeasureText(text) > maxWidth) text = text[..^2].TrimEnd() + "…";
        c.DrawText(text, x, y, font, paint);
        var w = font.MeasureText(text);
        if (strike) { paint.StrokeWidth = 3; c.DrawLine(x, y - size * 0.32f, x + w, y - size * 0.32f, paint); }
        return x + w;
    }

    /// <summary>Wraps text over up to <paramref name="maxLines"/> lines; returns the last baseline.</summary>
    private static float Wrapped(SKCanvas c, string text, float x, float y, float size, SKColor color, float width, int maxLines, bool bold = false)
    {
        using var font = Font(size, bold);
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var tryLine = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureText(tryLine) <= width || line.Length == 0) line = tryLine;
            else { lines.Add(line); line = word; }
        }
        if (line.Length > 0) lines.Add(line);
        if (lines.Count > maxLines) { lines = lines.Take(maxLines).ToList(); lines[^1] = lines[^1].TrimEnd('.', ',') + "…"; }
        for (var i = 0; i < lines.Count; i++) Text(c, lines[i], x, y + i * size * 1.18f, size, color, bold, maxWidth: width);
        return y + (lines.Count - 1) * size * 1.18f;
    }

    private static void Centered(SKCanvas c, string text, float y, float size, SKColor color, bool bold = false)
    {
        using var font = Font(size, bold);
        while (text.Length > 1 && font.MeasureText(text) > W - 120) text = text[..^2].TrimEnd() + "…";
        Text(c, text, (W - font.MeasureText(text)) / 2, y, size, color, bold);
    }

    private static void Pill(SKCanvas c, string text, float x, float top, float size, SKColor bg, SKColor fg)
    {
        using var font = Font(size, true);
        var w = font.MeasureText(text) + size * 1.2f;
        using var paint = new SKPaint { Color = bg, IsAntialias = true };
        c.DrawRoundRect(SKRect.Create(x, top, w, size * 1.6f), size * 0.8f, size * 0.8f, paint);
        Text(c, text, x + size * 0.6f, top + size * 1.15f, size, fg, bold: true);
    }

    private static void Save(SKBitmap bmp, string path)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var png = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        png.SaveTo(fs);
    }

    // ---------- FFmpeg ----------

    /// <summary>Photo slides: the photo zooms in (or out, alternating) inside its box over the slide's background and text.
    /// Slides cross-fade; music fades in and out. H.264 + AAC 48 kHz, closed 2-second GOP: what Instagram and Facebook ask for.</summary>
    private static List<string> Arguments(List<Slide> slides, string? music, string output)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var filters = new List<string>();
        var input = 0;
        var labels = new List<string>();
        for (var i = 0; i < slides.Count; i++)
        {
            var sl = slides[i];
            var secs = sl.Seconds.ToString(CultureInfo.InvariantCulture);
            args.AddRange(["-loop", "1", "-framerate", Fps.ToString(), "-t", secs, "-i", sl.Base]);
            var baseIn = input++;
            if (sl.Photo is null)
            {
                filters.Add($"[{baseIn}:v]format=yuv420p,setsar=1[s{i}]");
            }
            else
            {
                args.AddRange(["-i", sl.Photo]);
                var photoIn = input++;
                var frames = (int)Math.Round(sl.Seconds * Fps);
                var zoom = i % 2 == 0 ? $"1+0.07*on/{frames}" : $"1.07-0.07*on/{frames}";
                filters.Add($"[{photoIn}:v]zoompan=z='{zoom}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={frames}:s={BoxW}x{BoxH}:fps={Fps}[z{i}]");
                filters.Add($"[{baseIn}:v][z{i}]overlay={BoxX}:{BoxY}:shortest=1,format=yuv420p,setsar=1[s{i}]");
            }
            labels.Add($"s{i}");
        }
        // Cross-fades: each next slide starts fading in FadeSeconds before the previous one ends
        var last = labels[0];
        var at = slides[0].Seconds;
        for (var i = 1; i < slides.Count; i++)
        {
            var offset = (at - FadeSeconds).ToString("0.###", CultureInfo.InvariantCulture);
            var name = i == slides.Count - 1 ? "vout" : $"x{i}";
            filters.Add($"[{last}][{labels[i]}]xfade=transition=fade:duration={FadeSeconds.ToString(CultureInfo.InvariantCulture)}:offset={offset}[{name}]");
            last = name;
            at += slides[i].Seconds - FadeSeconds;
        }
        var total = at.ToString("0.###", CultureInfo.InvariantCulture);
        if (music is not null)
        {
            args.AddRange(["-stream_loop", "-1", "-i", music]);
            var fadeOut = Math.Max(0, at - 1.2).ToString("0.###", CultureInfo.InvariantCulture);
            filters.Add($"[{input}:a]atrim=0:{total},asetpts=N/SR/TB,afade=t=in:d=0.4,afade=t=out:st={fadeOut}:d=1.2,aresample=48000,aformat=channel_layouts=stereo[aout]");
        }
        else
        {
            args.AddRange(["-f", "lavfi", "-t", total, "-i", "anullsrc=r=48000:cl=stereo"]);
            filters.Add($"[{input}:a]anull[aout]");
        }
        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "medium", "-profile:v", "high", "-pix_fmt", "yuv420p", "-r", Fps.ToString(),
            "-g", (Fps * 2).ToString(), "-keyint_min", (Fps * 2).ToString(), "-sc_threshold", "0", "-flags", "+cgop",
            "-b:v", "6M", "-maxrate", "8M", "-bufsize", "12M",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
            "-t", total, "-movflags", "+faststart", output]);
        return args;
    }

    private async Task RunFfmpegAsync(List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(FfmpegPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("Could not start FFmpeg.");
        var errors = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new IOException("Making the reel took too long."); }
        if (p.ExitCode != 0)
        {
            var err = (await errors).Trim();
            logger.LogWarning("FFmpeg failed: {Error}", err);
            throw new IOException("FFmpeg could not make the reel: " + (err.Length > 300 ? err[^300..] : err));
        }
    }
}
