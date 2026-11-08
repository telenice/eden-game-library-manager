using Avalonia.Threading;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.App.Services;

public enum ConnectionState
{
    NotConfigured,
    Connecting,
    Online,
    Offline,
    Error,
}

/// <summary>
/// Single source of truth for the UI: the remote catalogue, the reconciled local library and the
/// match between them. All state changes happen on the UI thread and raise <see cref="Changed"/>.
/// </summary>
public sealed class LibraryCoordinator
{
    private readonly IGameLibraryProvider _provider;
    private readonly ILocalLibraryService _library;
    private readonly IManifestService _manifest;
    private readonly IDownloadManager _downloads;
    private readonly IConnectionProvider _connections;
    private readonly ILogger<LibraryCoordinator> _logger;
    private int _rescanQueued;
    private int _generation;
    private bool _refreshAgain;

    public LibraryCoordinator(
        IGameLibraryProvider provider,
        ILocalLibraryService library,
        IManifestService manifest,
        IDownloadManager downloads,
        IConnectionProvider connections,
        ILogger<LibraryCoordinator> logger)
    {
        _provider = provider;
        _library = library;
        _manifest = manifest;
        _downloads = downloads;
        _connections = connections;
        _logger = logger;

        _library.FilesChanged += (_, _) => QueueRescan();
        _downloads.TaskCompleted += (_, _) => Dispatcher.UIThread.Post(Recompute);
    }

    public CatalogResult? Catalog { get; private set; }
    public IReadOnlyList<LocalGame> LocalGames { get; private set; } = [];
    public IReadOnlyDictionary<string, MatchResult> Matches { get; private set; } = new Dictionary<string, MatchResult>();
    public ConnectionState State { get; private set; } = ConnectionState.NotConfigured;
    public string? StatusMessage { get; private set; }
    public bool IsRefreshing { get; private set; }

    public bool CanDownload => State == ConnectionState.Online;

    public event EventHandler? Changed;

    public async Task InitializeAsync(bool refreshCatalog)
    {
        await _manifest.InitializeAsync(CancellationToken.None);
        await RescanAsync();
        await _downloads.InitializeAsync(CancellationToken.None);
        _library.StartWatching();

        Catalog = await _provider.LoadCachedCatalogAsync(CancellationToken.None);
        if (_connections.GetConnection() is null)
            SetState(ConnectionState.NotConfigured, "Not configured");
        else if (Catalog is not null)
            SetState(ConnectionState.Offline, "Showing saved catalogue");
        Recompute();

        if (refreshCatalog) await RefreshCatalogAsync();
    }

    public async Task RefreshCatalogAsync()
    {
        if (IsRefreshing)
        {
            _refreshAgain = true;
            return;
        }

        IsRefreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                var generation = _generation;
                if (_connections.GetConnection() is null)
                {
                    SetState(ConnectionState.NotConfigured, "Configure your AeroFoil server in Settings.");
                    break;
                }

                SetState(ConnectionState.Connecting, "Connecting…");
                Recompute();
                try
                {
                    var result = await Task.Run(() => _provider.GetCatalogAsync(CancellationToken.None));
                    if (generation != _generation)
                    {
                        // The user switched profile while this request was running: discard it.
                        _refreshAgain = true;
                        continue;
                    }
                    Catalog = result;
                    if (result.FromCache)
                        SetState(ConnectionState.Offline, result.Warnings.FirstOrDefault() ?? "Offline");
                    else
                        SetState(ConnectionState.Online, $"Connected · {result.Items.Count} items");
                }
                catch (AeroFoilException ex)
                {
                    if (generation != _generation)
                    {
                        _refreshAgain = true;
                        continue;
                    }
                    _logger.LogWarning("Catalogue refresh failed: {Kind} {Message}", ex.Kind, ex.Message);
                    SetState(ex.IsConnectivityFailure ? ConnectionState.Offline : ConnectionState.Error, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error refreshing the catalogue");
                    SetState(ConnectionState.Error, $"Unexpected error: {ex.Message}");
                }
            } while (_refreshAgain);
        }
        finally
        {
            IsRefreshing = false;
            Recompute();
        }
    }

    /// <summary>Called after the active server profile changed: show its saved catalogue, then refresh.</summary>
    public async Task SwitchProfileAsync()
    {
        _generation++;
        Catalog = await _provider.LoadCachedCatalogAsync(CancellationToken.None);
        if (_connections.GetConnection() is null)
            SetState(ConnectionState.NotConfigured, "Not configured");
        else if (Catalog is not null)
            SetState(ConnectionState.Offline, "Showing saved catalogue");
        else
            SetState(ConnectionState.Connecting, "Connecting…");
        Recompute();
        await RefreshCatalogAsync();
    }

    public async Task RescanAsync()
    {
        try
        {
            LocalGames = await Task.Run(() => _library.ScanAsync(CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local library scan failed");
        }
        Recompute();
    }

    /// <summary>Recomputes matches from the in-memory library (cheap; no disk scan).</summary>
    public void Recompute()
    {
        LocalGames = _library.Games;
        Matches = Catalog is null
            ? new Dictionary<string, MatchResult>()
            : LibraryMatcher.MatchAll(Catalog.Items, LocalGames);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public RemoteGameItem? FindRemoteFor(LocalGame game) =>
        Catalog is null ? null : LibraryMatcher.FindRemoteFor(game, Catalog.Items);

    private void QueueRescan()
    {
        if (Interlocked.Exchange(ref _rescanQueued, 1) == 1) return;
        Dispatcher.UIThread.Post(async () =>
        {
            Interlocked.Exchange(ref _rescanQueued, 0);
            await RescanAsync();
        });
    }

    private void SetState(ConnectionState state, string? message)
    {
        State = state;
        StatusMessage = message;
    }
}
