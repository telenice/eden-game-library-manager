using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Storage;

/// <summary>
/// Keeps the manifest in sync with the games directory. The filesystem is authoritative: rows for
/// missing files are dropped, unknown game files are recorded as manually added, and a watcher
/// triggers rescans when files change outside the application.
/// </summary>
public sealed class LocalLibraryService : ILocalLibraryService, IDisposable
{
    private readonly IManifestService _manifest;
    private readonly ISettingsService _settings;
    private readonly ILogger<LocalLibraryService> _logger;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly Timer _debounce;
    private readonly object _gamesLock = new();
    private List<LocalGame> _games = [];
    private FileSystemWatcher? _watcher;
    private string? _watchedDirectory;

    public LocalLibraryService(IManifestService manifest, ISettingsService settings, ILogger<LocalLibraryService> logger)
    {
        _manifest = manifest;
        _settings = settings;
        _logger = logger;
        _debounce = new Timer(_ => FilesChanged?.Invoke(this, EventArgs.Empty));
        settings.Changed += (_, s) =>
        {
            if (!string.Equals(s.GamesDirectory, _watchedDirectory, StringComparison.OrdinalIgnoreCase))
                StartWatching();
        };
    }

    internal static TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    public event EventHandler? FilesChanged;

    public IReadOnlyList<LocalGame> Games
    {
        get
        {
            lock (_gamesLock) return _games;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<IReadOnlyList<LocalGame>> ScanAsync(CancellationToken cancellationToken)
    {
        await _scanLock.WaitAsync(cancellationToken);
        try
        {
            var directory = _settings.Current.GamesDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                SetGames([]);
                return Games;
            }

            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                // E.g. an unplugged external drive. Keep manifest rows untouched so nothing is "forgotten".
                _logger.LogWarning(ex, "Games directory {Directory} is not accessible", directory);
                SetGames([]);
                return Games;
            }

            var files = await Task.Run(() => EnumerateGameFiles(directory), cancellationToken);
            var records = await _manifest.GetLibraryItemsAsync(cancellationToken);
            var result = new List<LocalGame>();

            foreach (var record in records)
            {
                if (!PathSafety.IsWithinDirectory(directory, record.LocalPath))
                    continue; // belongs to a previously configured directory; kept in case it is selected again

                var path = Path.GetFullPath(record.LocalPath);
                if (files.Remove(path, out var info))
                {
                    var status = record.IsManaged && record.ExpectedSize is { } expected && expected != info.Length
                        ? LocalGameStatus.Unverified
                        : LocalGameStatus.Complete;
                    if (status != record.Status || info.Length != record.ActualSize)
                        await _manifest.UpdateLibraryItemStateAsync(record.Id, info.Length, status, cancellationToken);
                    result.Add(record with { ActualSize = info.Length, Status = status });
                }
                else
                {
                    _logger.LogInformation("File removed outside the app: {Path}", record.LocalPath);
                    await _manifest.DeleteLibraryItemAsync(record.Id, cancellationToken);
                }
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var (path, info) in files)
            {
                var identity = TitleIdHelper.ParseFileName(path);
                var game = new LocalGame
                {
                    LocalPath = path,
                    AppId = identity.AppId,
                    TitleId = TitleIdHelper.BaseTitleIdOf(identity.AppId),
                    ContentType = identity.ContentType,
                    Version = identity.Version,
                    ActualSize = info.Length,
                    Status = LocalGameStatus.Complete,
                    FirstSeenAt = now,
                    IsManaged = false,
                };
                result.Add(await _manifest.InsertLibraryItemIfMissingAsync(game, cancellationToken));
            }

            SetGames(result);
            return Games;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public async Task<LocalGame> RegisterDownloadAsync(LocalGame game, CancellationToken cancellationToken)
    {
        var stored = await _manifest.UpsertLibraryItemAsync(game, cancellationToken);
        Replace(stored);
        return stored;
    }

    public async Task DeleteAsync(LocalGame game, CancellationToken cancellationToken)
    {
        var directory = _settings.Current.GamesDirectory;
        if (string.IsNullOrWhiteSpace(directory))
            throw new UnsafePathException("No games directory is configured.");

        var path = Path.GetFullPath(game.LocalPath);
        if (!PathSafety.IsWithinDirectory(directory, path))
            throw new UnsafePathException("Refusing to delete a file outside the games directory.");
        if (!PathSafety.IsGameFile(path))
            throw new UnsafePathException("Refusing to delete a file that is not a game file.");
        if (PathSafety.HasReparsePoint(directory, path))
            throw new UnsafePathException("Refusing to delete through a symbolic link or junction.");

        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException ex)
            {
                throw new IOException($"Could not delete '{game.FileName}'. It may be in use by the emulator. ({ex.Message})", ex);
            }
        }

        if (game.Id > 0)
            await _manifest.DeleteLibraryItemAsync(game.Id, cancellationToken);

        _logger.LogInformation("Deleted local game {Path}", path);
        lock (_gamesLock) _games = _games.Where(g => !PathComparer.Equals(g.LocalPath, game.LocalPath)).ToList();
    }

    public async Task SetPinnedAsync(LocalGame game, bool pinned, CancellationToken cancellationToken)
    {
        await _manifest.SetPinnedAsync(game.Id, pinned, cancellationToken);
        Replace(game with { IsPinned = pinned });
    }

    public async Task<LocalGame> MoveAsync(LocalGame game, string targetDirectory, CancellationToken cancellationToken)
    {
        var directory = _settings.Current.GamesDirectory;
        if (string.IsNullOrWhiteSpace(directory))
            throw new UnsafePathException("No games directory is configured.");

        var source = Path.GetFullPath(game.LocalPath);
        var target = Path.GetFullPath(targetDirectory);
        if (!PathSafety.IsWithinDirectory(directory, source) || !PathSafety.IsGameFile(source))
            throw new UnsafePathException("Only game files inside the games directory can be moved.");
        if (!PathSafety.IsWithinDirectory(directory, Path.Combine(target, "x")))
            throw new UnsafePathException("The destination must be inside the games directory.");
        if (PathSafety.HasReparsePoint(directory, source) || PathSafety.HasReparsePoint(directory, target))
            throw new UnsafePathException("Refusing to move through a symbolic link or junction.");

        if (PathComparer.Equals(Path.GetDirectoryName(source), target.TrimEnd(Path.DirectorySeparatorChar)))
            return game;

        Directory.CreateDirectory(target);
        var destination = PathSafety.GetAvailablePath(target, Path.GetFileName(source));
        try
        {
            File.Move(source, destination, overwrite: false);
        }
        catch (IOException ex)
        {
            throw new IOException($"Could not move '{game.FileName}'. It may be in use by the emulator. ({ex.Message})", ex);
        }

        if (game.Id > 0)
            await _manifest.UpdateLibraryItemPathAsync(game.Id, destination, cancellationToken);

        var moved = game with { LocalPath = destination };
        lock (_gamesLock) _games = _games.Where(g => !PathComparer.Equals(g.LocalPath, game.LocalPath)).ToList();
        Replace(moved);
        _logger.LogInformation("Moved {Source} -> {Destination}", source, destination);
        return moved;
    }

    public async Task MarkAccessedAsync(LocalGame game, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await _manifest.SetLastAccessedAsync(game.Id, now, cancellationToken);
        Replace(game with { LastAccessedAt = now });
    }

    public bool IsFileInUse(LocalGame game)
    {
        try
        {
            using var stream = new FileStream(game.LocalPath, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watchedDirectory = _settings.Current.GamesDirectory;
        if (string.IsNullOrWhiteSpace(_watchedDirectory) || !Directory.Exists(_watchedDirectory)) return;

        try
        {
            var watcher = new FileSystemWatcher(_watchedDirectory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Changed += OnFileSystemEvent;
            watcher.Error += (_, e) =>
            {
                _logger.LogWarning(e.GetException(), "File watcher error; triggering a rescan");
                _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not watch {Directory}; changes will be picked up on refresh", _watchedDirectory);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
        _scanLock.Dispose();
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) =>
        _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);

    private Dictionary<string, FileInfo> EnumerateGameFiles(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Do not follow junctions or symbolic links out of the games directory.
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        var result = new Dictionary<string, FileInfo>(PathComparer);
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*", options))
            {
                // "._name.nsp" files are macOS AppleDouble metadata on FAT/exFAT drives, not games.
                if (!PathSafety.IsGameFile(path) || Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal)) continue;
                try
                {
                    var info = new FileInfo(path);
                    result[Path.GetFullPath(path)] = info;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Skipping unreadable file {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while scanning {Directory}", directory);
        }
        return result;
    }

    private void SetGames(List<LocalGame> games)
    {
        games.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        lock (_gamesLock) _games = games;
    }

    private void Replace(LocalGame game)
    {
        lock (_gamesLock)
        {
            var list = _games.Where(g => !PathComparer.Equals(g.LocalPath, game.LocalPath)).ToList();
            list.Add(game);
            list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            _games = list;
        }
    }
}
