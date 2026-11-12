using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Downloads;

/// <summary>
/// Persistent download queue. Each transfer streams into %LOCALAPPDATA%\...\Downloads\{id}.partial,
/// is verified, then moved into the games directory and recorded in the manifest. Partial files
/// never appear in the games directory.
/// </summary>
public sealed class DownloadManager : IDownloadManager
{
    private const long DiskSafetyMarginBytes = 64L * 1024 * 1024;

    private readonly HttpDownloader _downloader;
    private readonly IDownloadLinkResolver _links;
    private readonly IConnectionProvider _connections;
    private readonly IManifestService _manifest;
    private readonly ICacheManager _cache;
    private readonly ILocalLibraryService _library;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly BandwidthLimiter _limiter;
    private readonly ILogger<DownloadManager> _logger;

    private readonly object _lock = new();
    private readonly List<DownloadTask> _tasks = [];
    private readonly Dictionary<string, Task> _running = [];
    private bool _shuttingDown;

    public DownloadManager(
        HttpDownloader downloader,
        IDownloadLinkResolver links,
        IConnectionProvider connections,
        IManifestService manifest,
        ICacheManager cache,
        ILocalLibraryService library,
        ISettingsService settings,
        AppPaths paths,
        BandwidthLimiter limiter,
        ILogger<DownloadManager> logger)
    {
        _downloader = downloader;
        _links = links;
        _connections = connections;
        _manifest = manifest;
        _cache = cache;
        _library = library;
        _settings = settings;
        _paths = paths;
        _limiter = limiter;
        _logger = logger;

        ApplySettings(settings.Current);
        settings.Changed += (_, s) =>
        {
            ApplySettings(s);
            Pump();
        };
    }

    internal int MaxAttempts { get; set; } = 6;
    internal Func<int, TimeSpan> Backoff { get; set; } = attempt => TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, attempt - 1)));

    /// <summary>Wait before retrying when TorBox asks us to slow down.</summary>
    internal Func<int, TimeSpan> SlowDownBackoff { get; set; } = _ => TimeSpan.FromSeconds(5);

    /// <summary>Retries after a TorBox slow-down answer before the download fails (attempts = retries + 1).</summary>
    internal int SlowDownRetries { get; set; } = 6;

    public event EventHandler<DownloadTask>? TaskCompleted;
    public event EventHandler? TasksChanged;

    public IReadOnlyList<DownloadTask> Tasks
    {
        get
        {
            lock (_lock) return _tasks.ToList();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.DownloadsDirectory);
        var records = await _manifest.GetDownloadsAsync(cancellationToken);
        var autoResume = _settings.Current.AutoResumeDownloads;
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            foreach (var record in records)
            {
                if (_tasks.Any(t => t.Id == record.Id)) continue;
                var task = new DownloadTask(record.Id, record.Item, record.ServerKey, record.PartialPath, record.CreatedAt)
                {
                    ETag = record.ETag,
                    LastModified = record.LastModified,
                };
                if (record.TotalSize > 0) task.TotalBytes = record.TotalSize;
                task.DownloadedBytes = File.Exists(record.PartialPath) ? new FileInfo(record.PartialPath).Length : 0;
                task.Status = record.Status switch
                {
                    DownloadStatus.Failed => DownloadStatus.Failed,
                    DownloadStatus.Paused => DownloadStatus.Paused,
                    _ => autoResume ? DownloadStatus.Queued : DownloadStatus.Paused,
                };
                task.Message = record.Error;
                _tasks.Add(task);
                known.Add(Path.GetFullPath(record.PartialPath));
            }
        }

        // Partial files without a manifest row cannot be resumed safely; they live in our own folder.
        foreach (var orphan in Directory.EnumerateFiles(_paths.DownloadsDirectory, "*.partial"))
        {
            if (known.Contains(Path.GetFullPath(orphan))) continue;
            _logger.LogInformation("Removing orphaned partial download {File}", Path.GetFileName(orphan));
            TryDelete(orphan);
        }

        RaiseChanged();
        Pump();
    }

    public DownloadTask Enqueue(RemoteGameItem item)
    {
        var connection = _connections.GetConnection()
                         ?? throw new AeroFoilException(AeroFoilErrorKind.NotConfigured, "Connect to an AeroFoil server or TorBox account first.");

        // Validate up front so obviously unsafe catalogue entries fail immediately.
        _links.Validate(connection, item);
        PathSafety.SanitizeGameFileName(item.SourceFileName);

        DownloadTask task;
        lock (_lock)
        {
            var existing = _tasks.FirstOrDefault(t => t.Item.Key == item.Key && t.ServerKey == connection.ServerKey && !t.Status.IsTerminal());
            if (existing is not null) return existing;

            var id = Guid.NewGuid().ToString("N");
            task = new DownloadTask(id, item, connection.ServerKey, Path.Combine(_paths.DownloadsDirectory, id + ".partial"), DateTimeOffset.UtcNow)
            {
                Status = DownloadStatus.Queued,
            };
            _tasks.Add(task);
        }

        _logger.LogInformation("Queued download {Name} ({Key})", item.Name, item.Key);
        _ = PersistAsync(task);
        RaiseChanged();
        Pump();
        return task;
    }

    public void Pause(string taskId)
    {
        var task = Find(taskId);
        if (task is null) return;
        lock (_lock)
        {
            if (task.Cancellation is { } cts && task.Status == DownloadStatus.Downloading)
            {
                task.RequestedStop = StopReason.Pause;
                cts.Cancel();
                return;
            }
            if (task.Status != DownloadStatus.Queued) return;
            task.Status = DownloadStatus.Paused;
        }
        _ = PersistAsync(task);
        RaiseChanged();
    }

    public void Resume(string taskId)
    {
        var task = Find(taskId);
        if (task is null) return;
        lock (_lock)
        {
            if (task.Status is not (DownloadStatus.Paused or DownloadStatus.Failed)) return;
            task.Status = DownloadStatus.Queued;
            task.Message = null;
        }
        _ = PersistAsync(task);
        RaiseChanged();
        Pump();
    }

    public void Retry(string taskId) => Resume(taskId);

    public async Task CancelAsync(string taskId)
    {
        var task = Find(taskId);
        if (task is null) return;

        Task? running = null;
        lock (_lock)
        {
            if (task.Status.IsTerminal()) return;
            if (_running.TryGetValue(taskId, out var r) && task.Cancellation is { } cts)
            {
                task.RequestedStop = StopReason.Cancel;
                cts.Cancel();
                running = r;
            }
            else
            {
                task.Status = DownloadStatus.Cancelled;
            }
        }

        if (running is not null)
        {
            await running;
            return;
        }

        TryDelete(task.PartialPath);
        await SafeDeleteRecordAsync(task.Id);
        RaiseChanged();
    }

    public void ClearFinished()
    {
        lock (_lock) _tasks.RemoveAll(t => t.Status is DownloadStatus.Completed or DownloadStatus.Cancelled);
        RaiseChanged();
    }

    public bool IsQueuedOrActive(string itemKey)
    {
        var serverKey = _connections.GetConnection()?.ServerKey;
        lock (_lock) return _tasks.Any(t => t.Item.Key == itemKey && IsForServer(t, serverKey) && !t.Status.IsTerminal());
    }

    internal const string WaitingForProfileMessage = "Waiting: this download belongs to another server profile. Switch to that profile to continue.";

    private static bool IsForServer(DownloadTask task, string? serverKey) =>
        serverKey is not null && string.Equals(task.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase);

    public long GetReservedBytes(string? excludeTaskId = null)
    {
        lock (_lock)
        {
            return _tasks
                .Where(t => t.Id != excludeTaskId && t.Status.ReservesSpace())
                .Sum(t => Math.Max(t.TotalBytes, t.Item.Size));
        }
    }

    public async Task ShutdownAsync()
    {
        Task[] running;
        lock (_lock)
        {
            _shuttingDown = true;
            foreach (var task in _tasks.Where(t => t.Cancellation is not null))
            {
                task.RequestedStop = StopReason.Shutdown;
                task.Cancellation!.Cancel();
            }
            running = _running.Values.ToArray();
        }
        await Task.WhenAny(Task.WhenAll(running), Task.Delay(TimeSpan.FromSeconds(10)));
    }

    private void Pump()
    {
        lock (_lock)
        {
            if (_shuttingDown) return;
            var max = Math.Clamp(_settings.Current.MaxConcurrentDownloads, 1, 4);
            var serverKey = _connections.GetConnection()?.ServerKey;

            // Transfers queued under another server profile wait until that profile is active again.
            foreach (var waiting in _tasks.Where(t => t.Status == DownloadStatus.Queued && !IsForServer(t, serverKey)))
                waiting.Message = WaitingForProfileMessage;

            while (_running.Count < max)
            {
                var next = _tasks.FirstOrDefault(t => t.Status == DownloadStatus.Queued && !_running.ContainsKey(t.Id) && IsForServer(t, serverKey));
                if (next is null) break;
                next.Status = DownloadStatus.Downloading;
                next.RequestedStop = StopReason.None;
                next.Cancellation = new CancellationTokenSource();
                // Registered while holding the lock, so RunAsync's cleanup cannot run before this.
                _running[next.Id] = Task.Run(() => RunAsync(next));
            }
        }
    }

    private async Task RunAsync(DownloadTask task)
    {
        var cts = task.Cancellation!;
        var token = cts.Token;
        task.Message = null;
        RaiseChanged();

        try
        {
            var connection = _connections.GetConnection()
                             ?? throw new AeroFoilException(AeroFoilErrorKind.NotConfigured, "Not connected to an AeroFoil server or TorBox account.");
            if (!string.Equals(connection.ServerKey, task.ServerKey, StringComparison.OrdinalIgnoreCase))
                throw new AeroFoilException(AeroFoilErrorKind.AccessDenied,
                    "This download belongs to a different server profile than the one currently configured.");

            _links.Validate(connection, task.Item);
            var fileName = PathSafety.SanitizeGameFileName(task.Item.SourceFileName);
            var gamesDirectory = _settings.Current.GamesDirectory;
            if (string.IsNullOrWhiteSpace(gamesDirectory))
                throw new InvalidOperationException("Choose a games directory in Settings before downloading.");
            Directory.CreateDirectory(gamesDirectory);

            await CheckSpaceAsync(task, gamesDirectory, token);

            DownloadResult result;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Resolved per attempt: TorBox links expire, so a retry needs a fresh one.
                    var link = await _links.ResolveAsync(connection, task.Item, token);
                    result = await _downloader.DownloadAsync(
                        new DownloadRequest(link.Connection, link.Uri, task.PartialPath, task.Item.Size, task.ETag, task.LastModified),
                        (etag, lastModified, total) =>
                        {
                            task.ETag = etag;
                            task.LastModified = lastModified;
                            if (total is > 0) task.TotalBytes = total.Value;
                            _ = PersistAsync(task);
                        },
                        bytes => task.DownloadedBytes = bytes,
                        token);
                    break;
                }
                catch (Exception ex) when ((IsTransient(ex) || IsTorBoxSlowDown(ex, connection)) &&
                                           attempt < AttemptsFor(ex, connection) && !token.IsCancellationRequested)
                {
                    var slowDown = IsTorBoxSlowDown(ex, connection);
                    var delay = slowDown ? SlowDownBackoff(attempt) : Backoff(attempt);
                    _logger.LogWarning("Download {Name} attempt {Attempt} failed: {Error}. Retrying in {Delay}s",
                        task.Item.Name, attempt, ex.Message, delay.TotalSeconds);
                    task.Message = $"{ex.Message} Retrying in {delay.TotalSeconds:0}s (attempt {attempt + 1} of {AttemptsFor(ex, connection)}).";
                    RaiseChanged();
                    await Task.Delay(delay, token);
                    task.Message = null;
                }
            }

            task.Status = DownloadStatus.Verifying;
            RaiseChanged();

            var actual = new FileInfo(task.PartialPath).Length;
            if (actual != result.TotalBytes || (!result.SizeVerified && task.Item.Size > 0 && actual != task.Item.Size) || actual == 0)
            {
                TryDelete(task.PartialPath);
                throw new IncompleteDownloadException(
                    $"Verification failed: the file is {SizeFormatter.Format(actual)} but {SizeFormatter.Format(Math.Max(result.TotalBytes, task.Item.Size))} was expected.");
            }

            var targetDirectory = ResolveTargetDirectory(gamesDirectory, task.Item);
            var destination = PathSafety.EnsureWithinDirectory(gamesDirectory, PathSafety.GetAvailablePath(targetDirectory, fileName));
            await MoveIntoLibraryAsync(task.PartialPath, destination, token);

            var now = DateTimeOffset.UtcNow;
            var local = new LocalGame
            {
                LocalPath = destination,
                ServerKey = task.ServerKey,
                RemoteFileId = task.Item.FileId,
                RemoteUrl = task.Item.Url,
                TitleId = task.Item.TitleId,
                AppId = task.Item.EffectiveAppId,
                Name = task.Item.Name,
                ContentType = task.Item.ContentType,
                Version = task.Item.NumericVersion,
                OriginalFileName = task.Item.SourceFileName,
                ExpectedSize = actual,
                ActualSize = actual,
                Status = LocalGameStatus.Complete,
                DownloadedAt = now,
                FirstSeenAt = now,
                IsManaged = true,
                Verification = result.ETag is null ? $"size={actual}" : $"size={actual};etag={result.ETag}",
            };
            await _library.RegisterDownloadAsync(local, CancellationToken.None);
            await SafeDeleteRecordAsync(task.Id);

            task.CompletedPath = destination;
            task.TotalBytes = actual;
            task.DownloadedBytes = actual;
            task.Status = DownloadStatus.Completed;
            _logger.LogInformation("Download completed: {Name} -> {Path}", task.Item.Name, destination);
            TaskCompleted?.Invoke(this, task);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            switch (task.RequestedStop)
            {
                case StopReason.Cancel:
                    TryDelete(task.PartialPath);
                    task.Status = DownloadStatus.Cancelled;
                    task.DownloadedBytes = 0;
                    await SafeDeleteRecordAsync(task.Id);
                    _logger.LogInformation("Download cancelled: {Name}", task.Item.Name);
                    break;
                case StopReason.Shutdown:
                    // Persisted as queued so it resumes on the next launch when auto-resume is on.
                    task.Status = DownloadStatus.Queued;
                    await PersistAsync(task);
                    task.Status = DownloadStatus.Paused;
                    break;
                default:
                    task.Status = DownloadStatus.Paused;
                    await PersistAsync(task);
                    _logger.LogInformation("Download paused: {Name} at {Bytes} bytes", task.Item.Name, task.DownloadedBytes);
                    break;
            }
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.Message = FriendlyMessage(ex);
            _logger.LogError(ex, "Download failed: {Name}", task.Item.Name);
            await PersistAsync(task);
        }
        finally
        {
            lock (_lock)
            {
                _running.Remove(task.Id);
                task.Cancellation = null;
            }
            cts.Dispose();
            RaiseChanged();
            Pump();
        }
    }

    private async Task CheckSpaceAsync(DownloadTask task, string gamesDirectory, CancellationToken token)
    {
        var total = Math.Max(task.TotalBytes, task.Item.Size);
        if (total <= 0) return;

        var existing = File.Exists(task.PartialPath) ? new FileInfo(task.PartialPath).Length : 0;
        var remaining = Math.Max(0, total - existing);

        var downloadFree = GetFreeSpace(_paths.DownloadsDirectory);
        if (downloadFree is { } free && free < remaining + DiskSafetyMarginBytes)
            throw new InsufficientDiskSpaceException(remaining + DiskSafetyMarginBytes, free);

        if (!IsSameVolume(_paths.DownloadsDirectory, gamesDirectory) && GetFreeSpace(gamesDirectory) is { } gamesFree &&
            gamesFree < total + DiskSafetyMarginBytes)
            throw new InsufficientDiskSpaceException(total + DiskSafetyMarginBytes, gamesFree);

        await _cache.EnsureCapacityAsync(total, GetReservedBytes(task.Id), token);
    }

    /// <summary>
    /// Moves the verified file into the games directory. Across volumes the data is copied to a
    /// temporary name (which the emulator ignores) and only renamed to its final name once complete.
    /// </summary>
    private static async Task MoveIntoLibraryAsync(string source, string destination, CancellationToken token)
    {
        if (IsSameVolume(source, destination))
        {
            File.Move(source, destination, overwrite: false);
            return;
        }

        var temp = destination + ".egl-copying";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, 1 << 20, token);
            }
            File.Move(temp, destination, overwrite: false);
            File.Delete(source);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// The games directory itself, or "&lt;games dir&gt;/&lt;Game name&gt; [TitleID]" when organising by game.
    /// An existing folder that is a symlink/junction is refused rather than written through.
    /// </summary>
    private string ResolveTargetDirectory(string gamesDirectory, RemoteGameItem item)
    {
        if (!_settings.Current.OrganizeByGame) return gamesDirectory;

        var folder = PathSafety.EnsureWithinDirectory(gamesDirectory, Path.Combine(gamesDirectory, GameGrouping.FolderName(item)));
        if (PathSafety.HasReparsePoint(gamesDirectory, folder))
            throw new UnsafePathException($"Refusing to write into '{folder}': it is a symbolic link or junction.");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// TorBox answers too many requests with 429, and its link API and download servers have also been
    /// seen answering 400 under load or for a link that went stale. Each retry asks for a fresh link, so
    /// both are retried after a longer wait. AeroFoil's rate limit means repeated failed sign-ins, so
    /// it is never retried.
    /// </summary>
    private static bool IsTorBoxSlowDown(Exception ex, ServerConnection connection) =>
        connection.Kind == ServerKind.TorBox &&
        ex is AeroFoilException { Kind: AeroFoilErrorKind.RateLimited } or AeroFoilException { StatusCode: 400 or 429 };

    private int AttemptsFor(Exception ex, ServerConnection connection) =>
        IsTorBoxSlowDown(ex, connection) ? SlowDownRetries + 1 : MaxAttempts;

    private static bool IsTransient(Exception ex) => ex switch
    {
        AeroFoilException a => a.IsTransient,
        IncompleteDownloadException => true,
        HttpRequestException => true,
        _ => false,
    };

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        AeroFoilException or CacheLimitExceededException or InsufficientDiskSpaceException or UnsafePathException
            or IncompleteDownloadException or InvalidOperationException => ex.Message,
        UnauthorizedAccessException => $"Permission denied: {ex.Message}",
        IOException => $"File error: {ex.Message}",
        _ => $"Unexpected error: {ex.Message}",
    };

    private static long? GetFreeSpace(string path) => VolumeInfo.GetSpace(path)?.Free;

    private static bool IsSameVolume(string a, string b) => VolumeInfo.IsSameVolume(a, b);

    private void ApplySettings(AppSettings settings) =>
        _limiter.BytesPerSecond = Math.Max(0, settings.SpeedLimitKbps) * 1024L;

    private DownloadTask? Find(string id)
    {
        lock (_lock) return _tasks.FirstOrDefault(t => t.Id == id);
    }

    private async Task PersistAsync(DownloadTask task)
    {
        try
        {
            if (task.Status is DownloadStatus.Completed or DownloadStatus.Cancelled) return;
            await _manifest.SaveDownloadAsync(task.ToRecord(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist download state for {Id}", task.Id);
        }
    }

    private async Task SafeDeleteRecordAsync(string id)
    {
        try
        {
            await _manifest.DeleteDownloadAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove download record {Id}", id);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort: a leftover temp file is removed on the next start.
        }
    }

    private void RaiseChanged() => TasksChanged?.Invoke(this, EventArgs.Empty);
}
