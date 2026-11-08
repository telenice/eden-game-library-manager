using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.AeroFoil;

/// <summary>Title details plus an on-disk artwork cache (icons and screenshots from the AeroFoil server only).</summary>
public sealed class GameMetadataService(
    IAeroFoilClient client,
    IConnectionProvider connections,
    AppPaths paths,
    ILogger<GameMetadataService> logger) : IGameMetadataService
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, TitleDetails?> _details = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string?>> _inflight = new();
    private readonly SemaphoreSlim _throttle = new(6, 6);

    public async Task<TitleDetails?> GetDetailsAsync(string titleId, CancellationToken cancellationToken)
    {
        if (_details.TryGetValue(titleId, out var cached)) return cached;
        var connection = connections.GetConnection();
        if (connection is null) return null;
        try
        {
            var details = await client.GetTitleInfoAsync(connection, titleId, cancellationToken);
            _details[titleId] = details;
            return details;
        }
        catch (AeroFoilException ex)
        {
            logger.LogDebug("Title info for {TitleId} unavailable: {Error}", titleId, ex.Message);
            return null;
        }
    }

    public Task<string?> GetImageFileAsync(string url, CancellationToken cancellationToken)
    {
        var connection = connections.GetConnection();
        if (connection is null || string.IsNullOrWhiteSpace(url)) return Task.FromResult<string?>(null);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.ServerKey + "|" + url)))[..32].ToLowerInvariant();
        var path = Path.Combine(paths.ArtworkDirectory, key + ".img");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return Task.FromResult<string?>(path);

        var task = _inflight.GetOrAdd(key, _ => FetchAsync(connection, url, path, cancellationToken));
        _ = task.ContinueWith(_ => _inflight.TryRemove(key, out Task<string?>? _), TaskScheduler.Default);
        return task;
    }

    private async Task<string?> FetchAsync(ServerConnection connection, string url, string path, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var bytes = await client.GetResourceAsync(connection, url, MaxImageBytes, ct);
            if (!LooksLikeImage(bytes)) return null;
            Directory.CreateDirectory(paths.ArtworkDirectory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is AeroFoilException or IOException)
        {
            logger.LogDebug("Artwork {Url} unavailable: {Error}", url, ex.Message);
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    internal static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||           // PNG
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||                            // JPEG
        (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') ||                               // GIF
        (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
         b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') ||                              // WebP
        (b.Length > 2 && b[0] == 'B' && b[1] == 'M');                                                // BMP
}
