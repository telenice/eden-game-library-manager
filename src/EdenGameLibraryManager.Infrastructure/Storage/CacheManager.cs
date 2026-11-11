using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.Infrastructure.Storage;

/// <summary>
/// Enforces the storage limit ("Maximum storage for downloads"). Only games this application
/// downloaded count toward it. Nothing is ever deleted automatically: when a download doesn't fit,
/// the user chooses what to remove.
/// </summary>
public sealed class CacheManager(ILocalLibraryService library, ISettingsService settings) : ICacheManager
{
    public async Task<StorageInfo> GetStorageInfoAsync(long reservedBytes, CancellationToken cancellationToken)
    {
        var games = library.Games;
        var dir = settings.Current.GamesDirectory;
        // Querying the volume can block for seconds (a sleeping hard drive spinning up, a network share),
        // and callers are on the UI thread, so keep it off the caller's thread.
        var space = string.IsNullOrWhiteSpace(dir) ? null : await Task.Run(() => VolumeInfo.GetSpace(dir), cancellationToken);
        long? diskFree = space?.Free, diskTotal = space?.Total;

        return new StorageInfo
        {
            CacheLimitBytes = settings.Current.CacheLimitBytes,
            ManagedBytes = games.Where(g => g.IsManaged).Sum(g => g.ActualSize),
            UnmanagedBytes = games.Where(g => !g.IsManaged).Sum(g => g.ActualSize),
            ReservedBytes = reservedBytes,
            DiskFreeBytes = diskFree,
            DiskTotalBytes = diskTotal,
            GameCount = games.Count,
            PinnedCount = games.Count(g => g.IsPinned),
        };
    }

    public CachePlan Plan(long requiredBytes, long reservedBytes)
    {
        var games = library.Games;
        var managed = games.Where(g => g.IsManaged).Sum(g => g.ActualSize);
        return CachePlanner.Plan(settings.Current.CacheLimitBytes, managed, reservedBytes, requiredBytes, games);
    }

    public Task EnsureCapacityAsync(long requiredBytes, long reservedBytes, CancellationToken cancellationToken)
    {
        var plan = Plan(requiredBytes, reservedBytes);
        return plan.Fits ? Task.CompletedTask : Task.FromException(new CacheLimitExceededException(plan));
    }
}
