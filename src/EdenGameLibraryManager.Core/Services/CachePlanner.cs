using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Core.Services;

/// <summary>
/// Pure least-recently-used planning for the managed game cache. Only files this application
/// downloaded are eligible; pinned, protected (in use / active) and manually added files never are.
/// </summary>
public static class CachePlanner
{
    public static CachePlan Plan(
        long limitBytes,
        long managedBytes,
        long reservedBytes,
        long requiredBytes,
        IEnumerable<LocalGame> games,
        Func<LocalGame, bool>? isProtected = null)
    {
        if (limitBytes <= 0)
            return new CachePlan { RequiredBytes = requiredBytes, BytesToFree = 0 };

        var bytesToFree = managedBytes + reservedBytes + requiredBytes - limitBytes;
        if (bytesToFree <= 0)
            return new CachePlan { RequiredBytes = requiredBytes, BytesToFree = 0 };

        var eligible = EligibleForEviction(games, isProtected);

        var chosen = new List<LocalGame>();
        long freed = 0;
        foreach (var game in eligible)
        {
            if (freed >= bytesToFree) break;
            chosen.Add(game);
            freed += game.ActualSize;
        }

        return new CachePlan { RequiredBytes = requiredBytes, BytesToFree = bytesToFree, Evictions = chosen };
    }

    public static IEnumerable<LocalGame> EligibleForEviction(IEnumerable<LocalGame> games, Func<LocalGame, bool>? isProtected = null) =>
        games
            .Where(g => g.IsManaged && !g.IsPinned && (isProtected is null || !isProtected(g)))
            .OrderBy(g => g.LastUsed)
            .ThenBy(g => g.Id);
}
