using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Storage;

/// <summary>
/// Enforces the managed cache limit. Only games this application downloaded count toward the
/// limit and only they can be evicted; manually added files are never touched automatically.
/// </summary>
public sealed class CacheManager(ILocalLibraryService library, ISettingsService settings, ILogger<CacheManager> logger) : ICacheManager
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<StorageInfo> GetStorageInfoAsync(long reservedBytes, CancellationToken cancellationToken)
    {
        var games = library.Games;
        var dir = settings.Current.GamesDirectory;
        var space = string.IsNullOrWhiteSpace(dir) ? null : VolumeInfo.GetSpace(dir);
        long? diskFree = space?.Free, diskTotal = space?.Total;

        return Task.FromResult(new StorageInfo
        {
            CacheLimitBytes = settings.Current.CacheLimitBytes,
            ManagedBytes = games.Where(g => g.IsManaged).Sum(g => g.ActualSize),
            UnmanagedBytes = games.Where(g => !g.IsManaged).Sum(g => g.ActualSize),
            ReservedBytes = reservedBytes,
            DiskFreeBytes = diskFree,
            DiskTotalBytes = diskTotal,
            GameCount = games.Count,
            PinnedCount = games.Count(g => g.IsPinned),
        });
    }

    public CachePlan Plan(long requiredBytes, long reservedBytes)
    {
        var games = library.Games;
        var managed = games.Where(g => g.IsManaged).Sum(g => g.ActualSize);
        return CachePlanner.Plan(settings.Current.CacheLimitBytes, managed, reservedBytes, requiredBytes, games);
    }

    public async Task EnsureCapacityAsync(long requiredBytes, long reservedBytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var plan = Plan(requiredBytes, reservedBytes);
            if (plan.Fits) return;

            if (!settings.Current.AutoCleanup)
                throw new CacheLimitExceededException(plan);

            // Walk LRU order, skipping files that are currently open (e.g. by the emulator).
            long freed = 0;
            var toDelete = new List<LocalGame>();
            foreach (var game in CachePlanner.EligibleForEviction(library.Games))
            {
                if (freed >= plan.BytesToFree) break;
                if (library.IsFileInUse(game)) continue;
                toDelete.Add(game);
                freed += game.ActualSize;
            }

            if (freed < plan.BytesToFree)
                throw new CacheLimitExceededException(plan with { Evictions = toDelete });

            foreach (var game in toDelete)
            {
                logger.LogInformation("Automatic cleanup: removing least recently used {Name} ({Size})", game.DisplayName, SizeFormatter.Format(game.ActualSize));
                await library.DeleteAsync(game, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
