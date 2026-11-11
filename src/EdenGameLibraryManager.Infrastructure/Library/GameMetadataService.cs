using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Library;

/// <summary>
/// Title details plus an on-disk artwork cache. Details come from the AeroFoil server when the active
/// profile is AeroFoil and it has them, otherwise from titledb. Images come from the configured AeroFoil
/// server (with its credentials) or from titledb's public image host (with none).
/// </summary>
public sealed class GameMetadataService(
    IAeroFoilClient client,
    ITitleDatabase titles,
    HttpClient http,
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
        var connection = connections.GetConnection();
        if (connection is null) return null;
        var cacheKey = connection.ServerKey + "|" + titleId;
        if (_details.TryGetValue(cacheKey, out var cached)) return cached;

        TitleDetails? details = null;
        if (connection.Kind == ServerKind.AeroFoil)
        {
            try
            {
                details = await client.GetTitleInfoAsync(connection, titleId, cancellationToken);
            }
            catch (AeroFoilException ex)
            {
                logger.LogDebug("Title info for {TitleId} unavailable: {Error}", titleId, ex.Message);
            }
        }

        if (details is null || (string.IsNullOrWhiteSpace(details.Description) && details.ScreenshotUrls.Count == 0))
        {
            var found = await titles.LookupAsync([titleId], cancellationToken);
            if (found.Values.FirstOrDefault() is { } info)
                details = new TitleDetails
                {
                    TitleId = info.Id,
                    Description = info.Description,
                    ScreenshotUrls = info.ScreenshotUrls,
                };
        }

        // Misses aren't cached: titledb may still be downloading.
        if (details is not null) _details[cacheKey] = details;
        return details;
    }

    public Task<string?> GetImageFileAsync(string url, CancellationToken cancellationToken)
    {
        var connection = connections.GetConnection();
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<string?>(null);

        // titledb artwork is public and the same for every profile, so it is cached once.
        var isPublic = UrlPolicy.IsPublicArtworkUrl(url);
        if (!isPublic && connection?.Kind != ServerKind.AeroFoil) return Task.FromResult<string?>(null);

        var scope = isPublic ? "public" : connection!.ServerKey;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "|" + url)))[..32].ToLowerInvariant();
        var path = Path.Combine(paths.ArtworkDirectory, key + ".img");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return Task.FromResult<string?>(path);

        var task = _inflight.GetOrAdd(key, _ => FetchAsync(isPublic ? null : connection, url, path, cancellationToken));
        _ = task.ContinueWith(_ => _inflight.TryRemove(key, out Task<string?>? _), TaskScheduler.Default);
        return task;
    }

    /// <param name="connection">The AeroFoil server to fetch from, or null for a public titledb image.</param>
    private async Task<string?> FetchAsync(ServerConnection? connection, string url, string path, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var bytes = connection is null
                ? await GetPublicImageAsync(url, ct)
                : await client.GetResourceAsync(connection, url, MaxImageBytes, ct);
            if (!LooksLikeImage(bytes)) return null;
            Directory.CreateDirectory(paths.ArtworkDirectory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is AeroFoilException or IOException or HttpRequestException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug("Artwork {Url} unavailable: {Error}", url, ex.Message);
            return null;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private async Task<byte[]> GetPublicImageAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new AeroFoilException(AeroFoilErrorKind.NotFound, $"HTTP {(int)response.StatusCode}");
        return await AeroFoilResponses.ReadLimitedAsync(response.Content, MaxImageBytes, timeout.Token);
    }

    internal static bool LooksLikeImage(ReadOnlySpan<byte> b) =>
        (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||           // PNG
        (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) ||                            // JPEG
        (b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F') ||                               // GIF
        (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' &&
         b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') ||                              // WebP
        (b.Length > 2 && b[0] == 'B' && b[1] == 'M');                                                // BMP
}
