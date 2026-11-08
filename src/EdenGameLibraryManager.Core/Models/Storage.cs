namespace EdenGameLibraryManager.Core.Models;

public sealed record StorageInfo
{
    /// <summary>0 means unlimited.</summary>
    public long CacheLimitBytes { get; init; }
    /// <summary>Bytes used by game files this app downloaded.</summary>
    public long ManagedBytes { get; init; }
    /// <summary>Bytes reserved for queued, paused and active transfers.</summary>
    public long ReservedBytes { get; init; }
    /// <summary>Bytes used by files the user added manually (not counted toward the limit).</summary>
    public long UnmanagedBytes { get; init; }
    public long? DiskFreeBytes { get; init; }
    public long? DiskTotalBytes { get; init; }
    public int GameCount { get; init; }
    public int PinnedCount { get; init; }

    public long UsedBytes => ManagedBytes + ReservedBytes;
    public long? FreeUnderLimitBytes => CacheLimitBytes <= 0 ? null : Math.Max(0, CacheLimitBytes - UsedBytes);
}

/// <summary>Result of planning room for a new download within the cache limit.</summary>
public sealed record CachePlan
{
    public required long RequiredBytes { get; init; }
    /// <summary>Bytes that must be freed to fit under the limit (0 if it already fits).</summary>
    public required long BytesToFree { get; init; }
    /// <summary>LRU eviction candidates chosen to cover <see cref="BytesToFree"/>; may be insufficient.</summary>
    public IReadOnlyList<LocalGame> Evictions { get; init; } = [];
    public bool Fits => BytesToFree <= 0;
    public long EvictionBytes => Evictions.Sum(e => e.ActualSize);
    public bool CanSatisfyByEviction => Fits || EvictionBytes >= BytesToFree;
    public long Shortfall => Fits ? 0 : Math.Max(0, BytesToFree - EvictionBytes);
}
