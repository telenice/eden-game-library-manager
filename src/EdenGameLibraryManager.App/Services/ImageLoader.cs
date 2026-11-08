using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace EdenGameLibraryManager.App.Services;

/// <summary>Decodes cached artwork files into scaled bitmaps, keeping one instance per file.</summary>
public static class ImageLoader
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<Bitmap?> LoadAsync(string? path, int width) =>
        string.IsNullOrEmpty(path)
            ? Task.FromResult<Bitmap?>(null)
            : Task.Run(() =>
            {
                var key = $"{path}|{width}";
                if (Cache.TryGetValue(key, out var cached)) return cached;
                try
                {
                    using var stream = File.OpenRead(path);
                    var bitmap = Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
                    return Cache.GetOrAdd(key, bitmap);
                }
                catch (Exception)
                {
                    return (Bitmap?)null;
                }
            });
}
