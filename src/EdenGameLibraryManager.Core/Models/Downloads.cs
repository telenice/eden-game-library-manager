namespace EdenGameLibraryManager.Core.Models;

public enum DownloadStatus
{
    Queued = 0,
    Downloading = 1,
    Paused = 2,
    Verifying = 3,
    Completed = 4,
    Cancelled = 5,
    Failed = 6,
}

public static class DownloadStatusExtensions
{
    public static bool IsTerminal(this DownloadStatus s) =>
        s is DownloadStatus.Completed or DownloadStatus.Cancelled or DownloadStatus.Failed;

    /// <summary>States whose bytes count towards the cache reservation.</summary>
    public static bool ReservesSpace(this DownloadStatus s) =>
        s is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Verifying;
}

/// <summary>Persisted state of an unfinished transfer (survives restarts).</summary>
public sealed record DownloadRecord
{
    public required string Id { get; init; }
    public required string ServerKey { get; init; }
    public required RemoteGameItem Item { get; init; }
    public required string PartialPath { get; init; }
    public long TotalSize { get; init; }
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public DownloadStatus Status { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public enum StopReason
{
    None,
    Pause,
    Cancel,
    Shutdown,
}

/// <summary>
/// Live state of a transfer. Properties are updated from worker threads; UI code samples them on
/// its own thread (the desktop app polls on a timer) instead of binding to them directly.
/// </summary>
public sealed class DownloadTask
{
    private long _downloadedBytes;
    private long _totalBytes;

    public DownloadTask(string id, RemoteGameItem item, string serverKey, string partialPath, DateTimeOffset createdAt)
    {
        Id = id;
        Item = item;
        ServerKey = serverKey;
        PartialPath = partialPath;
        CreatedAt = createdAt;
        _totalBytes = item.Size;
    }

    public string Id { get; }
    public RemoteGameItem Item { get; }
    public string ServerKey { get; }
    public string PartialPath { get; }
    public DateTimeOffset CreatedAt { get; }

    public string? ETag { get; set; }
    public string? LastModified { get; set; }

    /// <summary>Path of the finished file once the download completed.</summary>
    public string? CompletedPath { get; set; }

    private volatile DownloadStatus _status;
    private volatile string? _message;

    public DownloadStatus Status { get => _status; set => _status = value; }

    public long DownloadedBytes
    {
        get => Interlocked.Read(ref _downloadedBytes);
        set => Interlocked.Exchange(ref _downloadedBytes, value);
    }

    public long TotalBytes
    {
        get => Interlocked.Read(ref _totalBytes);
        set => Interlocked.Exchange(ref _totalBytes, value);
    }

    /// <summary>Human-readable detail: error text, retry countdown, etc.</summary>
    public string? Message { get => _message; set => _message = value; }

    internal CancellationTokenSource? Cancellation { get; set; }
    internal StopReason RequestedStop { get; set; }

    public DownloadRecord ToRecord() => new()
    {
        Id = Id,
        ServerKey = ServerKey,
        Item = Item,
        PartialPath = PartialPath,
        TotalSize = TotalBytes,
        ETag = ETag,
        LastModified = LastModified,
        Status = Status,
        Error = Status == DownloadStatus.Failed ? Message : null,
        CreatedAt = CreatedAt,
    };
}

/// <summary>
/// Where to fetch one file from. <see cref="Connection"/> decides which credentials go with the
/// request: the AeroFoil profile itself, or an anonymous connection for a TorBox download link.
/// </summary>
public sealed record DownloadLink(ServerConnection Connection, Uri Uri);
