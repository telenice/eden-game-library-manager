using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Core.Interfaces;

/// <summary>Supplies the currently configured server and credentials (null when not configured).</summary>
public interface IConnectionProvider
{
    ServerConnection? GetConnection();
}

/// <summary>Low-level AeroFoil HTTP API client. All requests stay on the connection's origin.</summary>
public interface IAeroFoilClient
{
    /// <summary>Fetches the catalogue (GET /api/remote/sections, falling back to /api/shop/sections).</summary>
    Task<CatalogResult> GetCatalogAsync(ServerConnection connection, CancellationToken cancellationToken);

    /// <summary>GET /api/shop/title-info/{titleId}; returns null when the server has no metadata.</summary>
    Task<TitleDetails?> GetTitleInfoAsync(ServerConnection connection, string titleId, CancellationToken cancellationToken);

    /// <summary>Downloads a small same-origin resource such as an icon or screenshot.</summary>
    Task<byte[]> GetResourceAsync(ServerConnection connection, string url, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Sends an authenticated request, following redirects only within the trusted origin.
    /// The caller owns the returned response.
    /// </summary>
    Task<HttpResponseMessage> SendAsync(ServerConnection connection, HttpRequestMessage request, CancellationToken cancellationToken);
}

/// <summary>The active profile's games, from an AeroFoil server or a TorBox account.</summary>
public interface IGameLibraryProvider
{
    string Name { get; }

    /// <summary>Retrieves the live catalogue, falling back to the cached copy when the server is unreachable.</summary>
    Task<CatalogResult> GetCatalogAsync(CancellationToken cancellationToken);

    Task<CatalogResult?> LoadCachedCatalogAsync(CancellationToken cancellationToken);

    /// <summary>Fetches the live catalogue of <paramref name="connection"/> without caching it (used by Test Connection).</summary>
    Task<CatalogResult> FetchCatalogAsync(ServerConnection connection, CancellationToken cancellationToken);
}

/// <summary>Turns a catalogue item into the request that fetches its file.</summary>
public interface IDownloadLinkResolver
{
    /// <summary>Throws when <paramref name="item"/> cannot be downloaded from <paramref name="connection"/>.</summary>
    void Validate(ServerConnection connection, RemoteGameItem item);

    /// <summary>Called before every attempt, because TorBox download links expire.</summary>
    Task<DownloadLink> ResolveAsync(ServerConnection connection, RemoteGameItem item, CancellationToken cancellationToken);
}

/// <summary>A local copy of titledb (names, descriptions and artwork by application id).</summary>
public interface ITitleDatabase
{
    /// <summary>Raised on a background thread after a refresh replaced the local copy.</summary>
    event EventHandler? Updated;

    TitleDbStatus GetStatus();

    /// <summary>Looks up several ids at once; missing ids are left out. Empty when titledb is off or not downloaded yet.</summary>
    Task<IReadOnlyDictionary<string, TitleInfo>> LookupAsync(IEnumerable<string> ids, CancellationToken cancellationToken);

    /// <summary>Downloads titledb when it is missing, older than a week, for another region, or when <paramref name="force"/> is set.</summary>
    Task RefreshAsync(bool force, CancellationToken cancellationToken);
}

public interface IGameMetadataService
{
    Task<TitleDetails?> GetDetailsAsync(string titleId, CancellationToken cancellationToken);

    /// <summary>Returns a local file path for the image at <paramref name="url"/>, downloading and caching it if needed.</summary>
    Task<string?> GetImageFileAsync(string url, CancellationToken cancellationToken);
}

public interface ILocalLibraryService
{
    IReadOnlyList<LocalGame> Games { get; }

    /// <summary>Raised (debounced, on a background thread) when files change in the games directory.</summary>
    event EventHandler? FilesChanged;

    /// <summary>Reconciles manifest rows with the files that actually exist on disk.</summary>
    Task<IReadOnlyList<LocalGame>> ScanAsync(CancellationToken cancellationToken);

    Task<LocalGame> RegisterDownloadAsync(LocalGame game, CancellationToken cancellationToken);

    Task DeleteAsync(LocalGame game, CancellationToken cancellationToken);

    Task SetPinnedAsync(LocalGame game, bool pinned, CancellationToken cancellationToken);

    /// <summary>
    /// Moves a game file into <paramref name="targetDirectory"/> (inside the games directory) without
    /// overwriting anything, and updates its manifest row. Returns the updated game.
    /// </summary>
    Task<LocalGame> MoveAsync(LocalGame game, string targetDirectory, CancellationToken cancellationToken);

    Task MarkAccessedAsync(LocalGame game, CancellationToken cancellationToken);

    void StartWatching();
}

public interface IDownloadManager
{
    IReadOnlyList<DownloadTask> Tasks { get; }

    /// <summary>Raised on a background thread after a file has been moved into the games directory.</summary>
    event EventHandler<DownloadTask>? TaskCompleted;

    /// <summary>Raised when tasks are added, removed or change state.</summary>
    event EventHandler? TasksChanged;

    Task InitializeAsync(CancellationToken cancellationToken);

    DownloadTask Enqueue(RemoteGameItem item);

    void Pause(string taskId);
    void Resume(string taskId);
    Task CancelAsync(string taskId);
    void Retry(string taskId);
    void ClearFinished();

    bool IsQueuedOrActive(string itemKey);

    /// <summary>Total size of unfinished transfers, which is reserved against the storage limit.</summary>
    long GetReservedBytes(string? excludeTaskId = null);

    Task ShutdownAsync();
}

public interface ICacheManager
{
    Task<StorageInfo> GetStorageInfoAsync(long reservedBytes, CancellationToken cancellationToken);

    CachePlan Plan(long requiredBytes, long reservedBytes);

    /// <summary>
    /// Throws <see cref="CacheLimitExceededException"/> when <paramref name="requiredBytes"/> doesn't fit
    /// under the storage limit. Games are never removed automatically; the user picks them.
    /// </summary>
    Task EnsureCapacityAsync(long requiredBytes, long reservedBytes, CancellationToken cancellationToken);
}

/// <summary>Checks GitHub for newer releases of this app and, on Windows, installs them in place.</summary>
public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>
    /// True when this copy can replace itself: a published Windows build in a folder it can write to.
    /// Elsewhere the user is sent to the release page instead.
    /// </summary>
    bool CanInstallInPlace { get; }

    /// <summary>Returns the latest release when it is newer than <see cref="CurrentVersion"/>, otherwise null.</summary>
    Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the package, verifies its signature and swaps in the new executable. Returns the path
    /// to start once this process has exited. Nothing is changed if any step fails.
    /// </summary>
    Task<string> InstallAsync(AppUpdate update, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Removes the previous executable and downloaded packages left by an earlier update.</summary>
    Task CleanUpAsync();
}

/// <summary>Reads how long each game has been played in Eden. Eden's files are only read, never changed.</summary>
public interface IPlayTimeService
{
    /// <summary>Total play time by base title id, or null when Eden's play-time file can't be found.</summary>
    Task<IReadOnlyDictionary<string, TimeSpan>?> GetPlayTimesAsync(CancellationToken cancellationToken);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? Changed;
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>Per-profile passwords: always kept in memory for the session, optionally in the OS secret store.</summary>
public interface ICredentialStore
{
    bool SupportsPersistence { get; }
    string? GetPassword(string profileId);

    /// <summary>Keeps the password for this session and, when <paramref name="persist"/> is true, in the OS secret store.</summary>
    void SetPassword(string profileId, string password, bool persist);

    void Clear(string profileId);
}

/// <summary>SQLite-backed record of local games and unfinished downloads.</summary>
public interface IManifestService
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<LocalGame>> GetLibraryItemsAsync(CancellationToken cancellationToken);
    Task<LocalGame> UpsertLibraryItemAsync(LocalGame game, CancellationToken cancellationToken);
    /// <summary>Inserts the row unless one already exists for the same path; returns the stored row.</summary>
    Task<LocalGame> InsertLibraryItemIfMissingAsync(LocalGame game, CancellationToken cancellationToken);
    Task UpdateLibraryItemStateAsync(long id, long actualSize, LocalGameStatus status, CancellationToken cancellationToken);
    Task SetPinnedAsync(long id, bool pinned, CancellationToken cancellationToken);
    Task SetLastAccessedAsync(long id, DateTimeOffset when, CancellationToken cancellationToken);
    Task UpdateLibraryItemPathAsync(long id, string localPath, CancellationToken cancellationToken);
    Task DeleteLibraryItemAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<DownloadRecord>> GetDownloadsAsync(CancellationToken cancellationToken);
    Task SaveDownloadAsync(DownloadRecord record, CancellationToken cancellationToken);
    Task DeleteDownloadAsync(string id, CancellationToken cancellationToken);
}
