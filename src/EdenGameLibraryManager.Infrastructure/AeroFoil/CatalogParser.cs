using System.Globalization;
using System.Text.Json;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.Infrastructure.AeroFoil;

internal sealed record ParsedCatalog(
    IReadOnlyList<RemoteGameItem> Items,
    bool HasAllSection,
    bool AllTruncated,
    int? AllTotal,
    int AllCount,
    bool AccountFrozen,
    int SkippedItems);

/// <summary>
/// Parses the AeroFoil / CyberFoil "sections" payload:
/// <c>{"sections":[{"id":"new","title":"New","items":[...]}, ..., {"id":"all","items":[...],"total":N,"truncated":false}]}</c>.
/// Sections overlap (an item in "new" also appears in "all"), so items are de-duplicated by file.
/// </summary>
internal static class CatalogParser
{
    public static ParsedCatalog Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "The server response does not contain a game catalogue.");

        var items = new List<RemoteGameItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasAll = false;
        var truncated = false;
        int? allTotal = null;
        var allCount = 0;
        var frozen = false;
        var skipped = 0;

        foreach (var section in sections.EnumerateArray())
        {
            if (section.ValueKind != JsonValueKind.Object) continue;
            var id = GetString(section, "id")?.ToLowerInvariant();
            if (id == "cheats") continue; // cheat text files are not games

            if (!section.TryGetProperty("items", out var sectionItems) || sectionItems.ValueKind != JsonValueKind.Array)
                continue;

            if (id == "all")
            {
                hasAll = true;
                truncated = GetBool(section, "truncated") ?? false;
                allTotal = (int?)GetLong(section, "total");
                allCount = sectionItems.GetArrayLength();
            }

            foreach (var element in sectionItems.EnumerateArray())
            {
                var item = TryParseItem(element, out var isFrozenPlaceholder);
                if (isFrozenPlaceholder)
                {
                    frozen = true;
                    continue;
                }
                if (item is null)
                {
                    skipped++;
                    continue;
                }
                if (seen.Add(item.Key)) items.Add(item);
            }
        }

        return new ParsedCatalog(items, hasAll, truncated, allTotal, allCount, frozen, skipped);
    }

    internal static RemoteGameItem? TryParseItem(JsonElement e, out bool frozenPlaceholder)
    {
        frozenPlaceholder = false;
        if (e.ValueKind != JsonValueKind.Object) return null;

        var url = GetString(e, "url");
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.StartsWith("/api/frozen/", StringComparison.OrdinalIgnoreCase))
        {
            frozenPlaceholder = true;
            return null;
        }
        if (url.StartsWith("/api/cheats/", StringComparison.OrdinalIgnoreCase)) return null;

        var titleId = TitleIdHelper.Normalize(GetString(e, "title_id") ?? GetString(e, "titleId"));
        var appId = TitleIdHelper.Normalize(GetString(e, "app_id") ?? GetString(e, "appId"));
        var filename = GetString(e, "filename");
        var rawType = GetString(e, "app_type") ?? GetString(e, "type");
        var contentType = TitleIdHelper.ParseContentType(rawType, appId ?? titleId);

        var item = new RemoteGameItem
        {
            Name = FirstNonEmpty(GetString(e, "name"), GetString(e, "title_name"), filename) ?? string.Empty,
            TitleName = GetString(e, "title_name"),
            TitleId = titleId,
            AppId = appId,
            AppVersion = GetString(e, "app_version") ?? GetString(e, "version"),
            ContentType = contentType,
            Category = GetCategory(e),
            Url = url.Trim(),
            Size = Math.Max(0, GetLong(e, "size") ?? 0),
            FileId = GetLong(e, "file_id"),
            Filename = filename,
            IconUrl = FirstNonEmpty(GetString(e, "icon_url"), GetString(e, "iconUrl")),
            ReleaseDate = ParseReleaseDate(e),
            DownloadCount = (int?)GetLong(e, "download_count"),
        };

        // Without a name, fall back to the file name carried in the URL fragment, then the ids.
        return item.Name.Length > 0
            ? item
            : item with { Name = FirstNonEmpty(item.SourceFileName, appId, titleId) ?? "Unknown" };
    }

    private static DateOnly? ParseReleaseDate(JsonElement e)
    {
        var raw = GetString(e, "release_date") ?? GetString(e, "releaseDate");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Length == 8 && DateOnly.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var compact))
            return compact;
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
            return DateOnly.FromDateTime(dt.UtcDateTime);
        return null;
    }

    private static string? GetCategory(JsonElement e)
    {
        if (!e.TryGetProperty("category", out var c)) return null;
        return c.ValueKind switch
        {
            JsonValueKind.String => NullIfEmpty(c.GetString()),
            JsonValueKind.Array => NullIfEmpty(string.Join(", ", c.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))),
            _ => null,
        };
    }

    internal static string? GetString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => NullIfEmpty(v.GetString()),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    internal static long? GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var l) => l,
            JsonValueKind.Number when v.TryGetDouble(out var d) && d is >= long.MinValue and <= long.MaxValue => (long)d,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) => s,
            _ => null,
        };
    }

    private static bool? GetBool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
