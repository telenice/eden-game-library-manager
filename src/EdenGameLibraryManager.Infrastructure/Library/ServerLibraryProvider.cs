using System.Text.Json;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.TorBox;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Library;

/// <summary>
/// The active profile's game library, from AeroFoil or TorBox. Successful catalogues are cached on
/// disk so the app can show the last known catalogue (clearly marked as possibly outdated) while the
/// server is unreachable. Names, artwork and release dates missing from the server are filled in
/// from titledb when it is available.
/// </summary>
public sealed class ServerLibraryProvider(
    IAeroFoilClient client,
    TorBoxClient torBox,
    ITitleDatabase titles,
    IConnectionProvider connections,
    AppPaths paths,
    ILogger<ServerLibraryProvider> logger) : IGameLibraryProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Name => connections.GetConnection()?.Kind == ServerKind.TorBox ? "TorBox" : "AeroFoil";

    public async Task<CatalogResult> GetCatalogAsync(CancellationToken cancellationToken)
    {
        var connection = connections.GetConnection()
                         ?? throw new AeroFoilException(AeroFoilErrorKind.NotConfigured, "Configure your AeroFoil server or TorBox account in Settings.");
        try
        {
            var result = await FetchCatalogAsync(connection, cancellationToken);
            await SaveCacheAsync(connection.ServerKey, result);
            return await EnrichAsync(connection, result, cancellationToken);
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

    public Task<CatalogResult> FetchCatalogAsync(ServerConnection connection, CancellationToken cancellationToken) =>
        connection.Kind == ServerKind.TorBox
            ? torBox.GetCatalogAsync(connection, cancellationToken)
            : client.GetCatalogAsync(connection, cancellationToken);

    public async Task<CatalogResult?> LoadCachedCatalogAsync(CancellationToken cancellationToken)
    {
        var connection = connections.GetConnection();
        if (connection is null) return null;
        var file = paths.CatalogCacheFileFor(connection.ServerKey);
        if (!File.Exists(file)) return null;
        try
        {
            CatalogCache? cache;
            await using (var stream = File.OpenRead(file))
                cache = await JsonSerializer.DeserializeAsync<CatalogCache>(stream, JsonOptions, cancellationToken);
            if (cache is null || !string.Equals(cache.ServerKey, connection.ServerKey, StringComparison.OrdinalIgnoreCase)) return null;
            return await EnrichAsync(connection, new CatalogResult
            {
                Items = cache.Items,
                RetrievedAt = cache.RetrievedAt,
                Endpoint = cache.Endpoint,
                IsComplete = cache.IsComplete,
                FromCache = true,
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Cached catalogue is unreadable");
            return null;
        }
    }

    private async Task<CatalogResult> EnrichAsync(ServerConnection connection, CatalogResult result, CancellationToken ct)
    {
        var ids = result.Items
            .SelectMany(i => new[] { i.EffectiveAppId, TitleIdHelper.BaseTitleIdOf(i.EffectiveAppId) ?? i.TitleId })
            .OfType<string>();
        var found = await titles.LookupAsync(ids, ct);
        if (found.Count == 0) return result;
        var preferTitleDb = connection.Kind == ServerKind.TorBox;
        return result with { Items = result.Items.Select(i => Enrich(i, found, preferTitleDb)).ToList() };
    }

    /// <summary>
    /// Fills in an item from titledb. TorBox items only have a file name, so their names are replaced
    /// by the eShop names; AeroFoil items keep everything the server sent and only gain missing fields.
    /// </summary>
    internal static RemoteGameItem Enrich(RemoteGameItem item, IReadOnlyDictionary<string, TitleInfo> titles, bool preferTitleDb)
    {
        var appId = TitleIdHelper.Normalize(item.EffectiveAppId);
        var baseId = TitleIdHelper.BaseTitleIdOf(appId) ?? TitleIdHelper.Normalize(item.TitleId);
        var own = appId is not null && titles.TryGetValue(appId, out var o) ? o : null;
        var parent = baseId is not null && titles.TryGetValue(baseId, out var p) ? p : null;
        if (own is null && parent is null) return item;

        var name = item.Name;
        if (preferTitleDb || string.IsNullOrWhiteSpace(name))
        {
            name = item.ContentType switch
            {
                ContentType.Update when parent?.Name is { } baseName => $"{baseName} (Update)",
                ContentType.Dlc when own?.Name is { } dlcName => dlcName,
                ContentType.Dlc when parent?.Name is { } baseName => $"{baseName} (DLC)",
                _ => own?.Name ?? parent?.Name ?? item.Name,
            };
        }

        return item with
        {
            Name = name,
            TitleName = string.IsNullOrWhiteSpace(item.TitleName) ? parent?.Name : item.TitleName,
            TitleId = item.TitleId ?? baseId,
            IconUrl = item.IconUrl ?? own?.IconUrl ?? parent?.IconUrl,
            ReleaseDate = item.ReleaseDate ?? own?.ReleaseDate ?? parent?.ReleaseDate,
            Category = item.Category ?? parent?.Category,
        };
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
