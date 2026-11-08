using System.Globalization;
using System.Text.RegularExpressions;
using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Core.Services;

/// <summary>Parsed identity hints from a game file name such as "Game [0100ABCD12340000][v65536].nsp".</summary>
public sealed record FileNameIdentity(string? AppId, long? Version, ContentType ContentType);

public static partial class TitleIdHelper
{
    [GeneratedRegex(@"\[(?<id>[0-9A-Fa-f]{16})\]")]
    private static partial Regex BracketedIdRegex();

    [GeneratedRegex(@"\[v(?<ver>\d{1,10})\]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketedVersionRegex();

    public static bool IsValidId(string? id) =>
        id is { Length: 16 } && ulong.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    public static string? Normalize(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim().ToUpperInvariant();

    /// <summary>
    /// Classifies a Switch application id by its low 12 bits: 0x000 is the base application,
    /// 0x800 is its update, anything else is add-on content (DLC).
    /// </summary>
    public static ContentType ClassifyAppId(string? appId)
    {
        if (!IsValidId(appId)) return ContentType.Unknown;
        var value = ulong.Parse(appId!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (value & 0xFFF) switch
        {
            0x000 => ContentType.Base,
            0x800 => ContentType.Update,
            _ => ContentType.Dlc,
        };
    }

    /// <summary>Returns the base title id for a base, update or DLC application id.</summary>
    public static string? BaseTitleIdOf(string? appId)
    {
        if (!IsValidId(appId)) return null;
        var value = ulong.Parse(appId!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var baseValue = ClassifyAppId(appId) == ContentType.Dlc
            ? (value & ~0xFFFUL) - 0x1000
            : value & ~0xFFFUL;
        return baseValue.ToString("X16", CultureInfo.InvariantCulture);
    }

    public static FileNameIdentity ParseFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var idMatch = BracketedIdRegex().Match(name);
        var appId = idMatch.Success ? idMatch.Groups["id"].Value.ToUpperInvariant() : null;

        long? version = null;
        var verMatch = BracketedVersionRegex().Match(name);
        if (verMatch.Success && long.TryParse(verMatch.Groups["ver"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            version = v;

        return new FileNameIdentity(appId, version, ClassifyAppId(appId));
    }

    public static ContentType ParseContentType(string? raw, string? appId)
    {
        switch (raw?.Trim().ToUpperInvariant())
        {
            case "BASE" or "APPLICATION" or "128":
                return ContentType.Base;
            case "UPDATE" or "UPD" or "PATCH" or "129":
                return ContentType.Update;
            case "DLC" or "ADDON" or "ADD-ON" or "AOC" or "130":
                return ContentType.Dlc;
        }
        return ClassifyAppId(appId);
    }
}
