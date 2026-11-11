using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Core.Services;

/// <summary>
/// Groups base games with their updates and DLC. The base title id is derived from the application
/// id (Switch ids encode it: update = base | 0x800, DLC = base + 0x1000 + n), which stays correct even
/// when a server's <c>title_id</c> field is missing or points at the DLC itself; <c>title_id</c> is
/// only used when there is no valid application id.
/// </summary>
public static class GameGrouping
{
    public static string GroupKey(RemoteGameItem item) =>
        TitleIdHelper.BaseTitleIdOf(item.AppId)
        ?? TitleIdHelper.Normalize(item.TitleId)
        ?? item.Key;

    public static string GroupKey(LocalGame game) =>
        TitleIdHelper.BaseTitleIdOf(game.AppId)
        ?? TitleIdHelper.Normalize(game.TitleId)
        ?? "path:" + game.LocalPath;

    /// <summary>
    /// The updates and DLC on disk that belong to <paramref name="game"/> when it is a base game. Empty
    /// for an update or DLC: those never pull in the base game or each other.
    /// </summary>
    public static IReadOnlyList<LocalGame> CompanionsOf(LocalGame game, IEnumerable<LocalGame> games)
    {
        if (game.ContentType != ContentType.Base) return [];
        var key = GroupKey(game);
        return games
            .Where(g => g.ContentType is ContentType.Update or ContentType.Dlc &&
                        !string.Equals(g.LocalPath, game.LocalPath, StringComparison.OrdinalIgnoreCase) &&
                        GroupKey(g) == key)
            .ToList();
    }

    /// <summary>
    /// The per-game folder for a catalogue item: "&lt;base game name&gt; [&lt;base title id&gt;]".
    /// AeroFoil's <c>title_name</c> is the base game's name for updates and DLC too, so all
    /// content for a title lands in the same folder.
    /// </summary>
    public static string FolderName(RemoteGameItem item) =>
        FolderName(!string.IsNullOrWhiteSpace(item.TitleName) ? item.TitleName! : item.Name, GroupKey(item));

    public static string FolderName(string gameName, string groupKey) =>
        TitleIdHelper.IsValidId(groupKey)
            ? $"{PathSafety.SanitizeFolderName(gameName, 100)} [{groupKey}]"
            : PathSafety.SanitizeFolderName(gameName, 120);

    /// <summary>Base first, then updates (newest first), then DLC by name.</summary>
    public static int ContentOrder(ContentType type) => type switch
    {
        ContentType.Base => 0,
        ContentType.Update => 1,
        ContentType.Dlc => 2,
        _ => 3,
    };

    public static IEnumerable<RemoteGameItem> OrderMembers(IEnumerable<RemoteGameItem> items) =>
        items.OrderBy(i => ContentOrder(i.ContentType))
            .ThenByDescending(i => i.ContentType == ContentType.Update ? i.NumericVersion : 0)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
}
