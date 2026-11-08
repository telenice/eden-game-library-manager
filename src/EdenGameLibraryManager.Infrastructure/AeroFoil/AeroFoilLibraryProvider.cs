using System.Text.Json;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.AeroFoil;

/// <summary>
/// The AeroFoil game library. Successful catalogues are cached on disk so the app can show the
/// last known catalogue (clearly marked as possibly outdated) while the server is unreachable.
/// </summary>
public sealed class AeroFoilLibraryProvider(
    IAeroFoilClient client,
    IConnectionProvider connections,
    AppPaths paths,
    ILogger<AeroFoilLibraryProvider> logger) : IGameLibraryProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => "AeroFoil";

    public async Task<CatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var connection = connections.GetConnection()
                         ?? throw new AeroFoilException(AeroFoilErrorKind.NotConfigured, "Configure your AeroFoil server in Settings.");
        try
        {
            var result = await client.GetCatalogAsync(connection, cancellationToken);
            await SaveCacheAsync(connection.ServerKey, result);
            return result;
        }
        catch (AeroFoilException ex) when (ex.IsConnectivityFailure || ex.Kind == AeroFoilErrorKind.ServerError)
        {
            var cached = await LoadCachedCatalogAsync(cancellationToken);
            if (cached is null) throw;
            logger.LogWarning("Server unavailable ({Error}); using cached catalogue from {Date}", ex.Message, cached.RetrievedAt);
            return cached with
            {
                Warnings = [$"Offline: {ex.Message} Showing the catalogue saved {cached.RetrievedAt.LocalDateTime:g}; it may be out of date.", .. cached.Warnings],
            };
        }
    }

    public async Task<CatalogResult?> LoadCachedCatalogAsync(CancellationToken cancellationToken)
    {
        var connection = connections.GetConnection();
        if (connection is null) return null;
        var file = paths.CatalogCacheFileFor(connection.ServerKey);
        if (!File.Exists(file)) return null;
        try
        {
            await using var stream = File.OpenRead(file);
            var cache = await JsonSerializer.DeserializeAsync<CatalogCache>(stream, JsonOptions, cancellationToken);
            if (cache is null || !string.Equals(cache.ServerKey, connection.ServerKey, StringComparison.OrdinalIgnoreCase)) return null;
            return new CatalogResult
            {
                Items = cache.Items,
                RetrievedAt = cache.RetrievedAt,
                Endpoint = cache.Endpoint,
                IsComplete = cache.IsComplete,
                FromCache = true,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Cached catalogue is unreadable");
            return null;
        }
    }

    private async Task SaveCacheAsync(string serverKey, CatalogResult result)
    {
        try
        {
            var cache = new CatalogCache(serverKey, result.RetrievedAt, result.Endpoint, result.IsComplete, result.Items.ToList());
            var file = paths.CatalogCacheFileFor(serverKey);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not cache the catalogue");
        }
    }

    private sealed record CatalogCache(string ServerKey, DateTimeOffset RetrievedAt, string? Endpoint, bool IsComplete, List<RemoteGameItem> Items);
}
