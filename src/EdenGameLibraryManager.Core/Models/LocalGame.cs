namespace EdenGameLibraryManager.Core.Models;

public enum LocalGameStatus
{
    /// <summary>File present and its size matches what was recorded at download time.</summary>
    Complete = 0,
    /// <summary>File present but it could not be verified (size changed since download).</summary>
    Unverified = 1,
}

/// <summary>
/// A game file in the local games directory. Rows are persisted in the manifest database, but the
/// filesystem is always authoritative: rows are reconciled against real files on every scan.
/// </summary>
public sealed record LocalGame
{
    public long Id { get; init; }
    public required string LocalPath { get; init; }
    public string? ServerKey { get; init; }
    public long? RemoteFileId { get; init; }
    public string? RemoteUrl { get; init; }
    public string? TitleId { get; init; }
    public string? AppId { get; init; }
    public string? Name { get; init; }
    public ContentType ContentType { get; init; }
    public long? Version { get; init; }
    public string? OriginalFileName { get; init; }
    public long? ExpectedSize { get; init; }
    public long ActualSize { get; init; }
    public LocalGameStatus Status { get; init; }
    public DateTimeOffset? DownloadedAt { get; init; }
    public DateTimeOffset? LastAccessedAt { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public bool IsPinned { get; init; }

    /// <summary>True when the file was downloaded by this application (and therefore may be evicted).</summary>
    public bool IsManaged { get; init; }

    public string? Verification { get; init; }

    public string FileName => Path.GetFileName(LocalPath);

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name! : Path.GetFileNameWithoutExtension(LocalPath);

    public string? ContentIdentity =>
        string.IsNullOrEmpty(AppId) || Version is null ? null : $"{AppId}|{ContentType}|{Version}";

    /// <summary>Best-known "last used" moment for LRU decisions.</summary>
    public DateTimeOffset LastUsed
    {
        get
        {
            var last = FirstSeenAt;
            if (DownloadedAt > last) last = DownloadedAt.Value;
            if (LastAccessedAt > last) last = LastAccessedAt.Value;
            return last;
        }
    }
}
