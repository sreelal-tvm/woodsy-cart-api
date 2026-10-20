namespace WoodsyCart.Infrastructure.Services;

/// <summary>Stores uploaded and downloaded files under {WebRoot}/media and returns public relative paths.</summary>
public class FileStorage(string webRootPath)
{
    public string Root { get; } = webRootPath;

    private static readonly HashSet<string> AllowedExtensions =
        [".jpg", ".jpeg", ".png", ".webp", ".gif", ".pdf", ".mp4"];

    public string FullPath(string relative) => Path.Combine(Root, relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public async Task<string> SaveAsync(Stream content, string folder, string fileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext)) throw new Common.AppException($"File type {ext} is not allowed.");
        var relative = $"/media/{folder.Trim('/')}/{Guid.NewGuid():N}{ext}";
        await WriteAsync(content, relative, ct);
        return relative;
    }

    public async Task WriteAsync(Stream content, string relative, CancellationToken ct = default)
    {
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
    }

    public void Delete(string? relative)
    {
        if (string.IsNullOrEmpty(relative) || !relative.StartsWith("/media/")) return;
        var full = FullPath(relative);
        if (File.Exists(full)) File.Delete(full);
    }
}
