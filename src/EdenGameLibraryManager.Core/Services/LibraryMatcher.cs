using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Core.Services;

public enum MatchState
{
    /// <summary>No local copy of this exact content.</summary>
    NotDownloaded,
    /// <summary>A verified local copy exists: hide it from Available Games.</summary>
    Downloaded,
    /// <summary>A local file looks related but could not be verified; the item stays available.</summary>
    PossibleMatch,
}

public sealed record MatchResult(MatchState State, LocalGame? Local)
{
    public static readonly MatchResult None = new(MatchState.NotDownloaded, null);
}

/// <summary>
/// Decides which remote catalogue items already exist locally. Matching is deliberately
/// conservative: an item is only treated as downloaded when the evidence identifies the same
/// content (app id + content type + version) or the exact same file (name and size).
/// A base game is never considered downloaded because its update or DLC is present, since those
/// have different application ids.
/// </summary>
public static class LibraryMatcher
{
    public static Dictionary<string, MatchResult> MatchAll(IEnumerable<RemoteGameItem> remote, IReadOnlyCollection<LocalGame> local)
    {
        var byAppId = local
            .Where(l => !string.IsNullOrEmpty(l.AppId))
            .ToLookup(l => l.AppId!, StringComparer.OrdinalIgnoreCase);
        var byFileName = local.ToLookup(l => l.FileName, StringComparer.OrdinalIgnoreCase);
        var byOriginalName = local
            .Where(l => !string.IsNullOrEmpty(l.OriginalFileName))
            .ToLookup(l => l.OriginalFileName!, StringComparer.OrdinalIgnoreCase);

        var results = new Dictionary<string, MatchResult>();
        foreach (var item in remote)
        {
            var appId = item.EffectiveAppId;
            var candidates = (appId is null ? [] : byAppId[appId])
                .Concat(item.SourceFileName is { } fn ? byFileName[fn].Concat(byOriginalName[fn]) : [])
                .Distinct();
            results[item.Key] = Match(item, candidates);
        }
        return results;
    }

    public static MatchResult Match(RemoteGameItem item, IEnumerable<LocalGame> candidates)
    {
        MatchResult? possible = null;
        foreach (var local in candidates)
        {
            var state = Compare(item, local);
            if (state == MatchState.Downloaded) return new MatchResult(MatchState.Downloaded, local);
            if (state == MatchState.PossibleMatch) possible ??= new MatchResult(MatchState.PossibleMatch, local);
        }
        return possible ?? MatchResult.None;
    }

    public static MatchState Compare(RemoteGameItem item, LocalGame local)
    {
        var sizeMatches = item.Size > 0 && local.ActualSize == item.Size;
        var remoteName = item.SourceFileName;

        // Same file name (as originally downloaded or as currently on disk) and identical size.
        if (sizeMatches && remoteName is not null &&
            (string.Equals(local.FileName, remoteName, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(local.OriginalFileName, remoteName, StringComparison.OrdinalIgnoreCase)))
            return MatchState.Downloaded;

        var appId = item.EffectiveAppId;
        if (appId is null || !string.Equals(local.AppId, appId, StringComparison.OrdinalIgnoreCase))
            return MatchState.NotDownloaded;

        // Different content categories (e.g. a DLC sharing an id prefix) never match.
        if (local.ContentType != ContentType.Unknown && item.ContentType != ContentType.Unknown &&
            local.ContentType != item.ContentType)
            return MatchState.NotDownloaded;

        if (local.IsManaged)
        {
            if (local.Version == item.NumericVersion)
                return local.Status == LocalGameStatus.Complete ? MatchState.Downloaded : MatchState.PossibleMatch;
            return MatchState.NotDownloaded;
        }

        // Manually added file: trust explicit [TitleID][vVersion] tokens, or id + identical size.
        if (local.Version is { } v)
            return v == item.NumericVersion ? MatchState.Downloaded : MatchState.NotDownloaded;

        return sizeMatches ? MatchState.Downloaded : MatchState.PossibleMatch;
    }

    /// <summary>Finds the catalogue entry that best describes a local file (for names and artwork).</summary>
    public static RemoteGameItem? FindRemoteFor(LocalGame local, IReadOnlyCollection<RemoteGameItem> remote)
    {
        if (local.RemoteFileId is { } fileId)
        {
            var byId = remote.FirstOrDefault(r => r.FileId == fileId && Compare(r, local) != MatchState.NotDownloaded);
            if (byId is not null) return byId;
        }
        RemoteGameItem? related = null;
        foreach (var r in remote)
        {
            var state = Compare(r, local);
            if (state == MatchState.Downloaded) return r;
            if (state == MatchState.PossibleMatch) related ??= r;
            else if (related is null && local.AppId is not null &&
                     string.Equals(r.EffectiveAppId, local.AppId, StringComparison.OrdinalIgnoreCase))
                related = r;
        }
        return related;
    }
}
