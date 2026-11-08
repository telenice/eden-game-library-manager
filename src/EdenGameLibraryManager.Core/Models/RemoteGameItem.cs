using System.Globalization;

namespace EdenGameLibraryManager.Core.Models;

/// <summary>
/// A single downloadable file advertised by the AeroFoil catalogue.
/// Every optional property may be missing from the server response.
/// </summary>
public sealed record RemoteGameItem
{
    public required string Name { get; init; }
    public string? TitleName { get; init; }
    public string? TitleId { get; init; }
    public string? AppId { get; init; }
    public string? AppVersion { get; init; }
    public ContentType ContentType { get; init; }
    public string? Category { get; init; }
    public required string Url { get; init; }
    public long Size { get; init; }
    public long? FileId { get; init; }
    public string? Filename { get; init; }
    public string? IconUrl { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? DownloadCount { get; init; }

    /// <summary>Unique key for the underlying downloadable file (used for de-duplication).</summary>
    public string Key => FileId is { } id ? $"file:{id}" : $"url:{StripFragment(Url)}";

    /// <summary>App id when present, otherwise the title id (base games share both).</summary>
    public string? EffectiveAppId =>
        !string.IsNullOrWhiteSpace(AppId) ? AppId : (ContentType == ContentType.Base ? TitleId : null);

    /// <summary>Content identity: same app id, content type and version means the same content.</summary>
    public string? ContentIdentity =>
        string.IsNullOrEmpty(EffectiveAppId) ? null : $"{EffectiveAppId}|{ContentType}|{NumericVersion}";

    public long NumericVersion =>
        long.TryParse(AppVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>The best available file name: explicit filename, then the URL fragment, then the last URL segment.</summary>
    public string? SourceFileName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Filename)) return Filename;
            var hash = Url.IndexOf('#');
            if (hash >= 0 && hash < Url.Length - 1) return Uri.UnescapeDataString(Url[(hash + 1)..]);
            var path = StripFragment(Url);
            var q = path.IndexOf('?');
            if (q >= 0) path = path[..q];
            var slash = path.LastIndexOf('/');
            var last = slash >= 0 ? path[(slash + 1)..] : path;
            return string.IsNullOrWhiteSpace(last) ? null : Uri.UnescapeDataString(last);
        }
    }

    public string FileExtension =>
        Path.GetExtension(SourceFileName ?? string.Empty).TrimStart('.').ToLowerInvariant();

    public string DisplayVersion => $"v{NumericVersion}";

    private static string StripFragment(string url)
    {
        var hash = url.IndexOf('#');
        return hash >= 0 ? url[..hash] : url;
    }
}
