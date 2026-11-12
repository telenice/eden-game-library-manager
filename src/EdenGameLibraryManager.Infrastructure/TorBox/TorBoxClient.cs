using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.TorBox;

/// <summary>
/// Client for the TorBox API (api.torbox.app/v1/api, checked against its OpenAPI document):
/// <list type="bullet">
/// <item><c>GET /{torrents|usenet|webdl}/mylist</c> lists the account's downloads and their files.</item>
/// <item><c>GET /{kind}/requestdl</c> returns a short-lived direct link to one file on TorBox's CDN.</item>
/// </list>
/// Files are fetched straight from that link, so they never pass through the user's own connection
/// or any relay. TorBox puts the API key in the link itself (<c>?token=</c>), so links must be HTTPS
/// and are never logged or stored. The download host is whatever TorBox's API returns; TorBox
/// doesn't publish a fixed list of download domains.
/// </summary>
public sealed partial class TorBoxClient(HttpClient http, ILogger<TorBoxClient> logger)
{
    public static readonly Uri ApiBaseUri = new("https://api.torbox.app/v1/api/");

    /// <summary>The download lists TorBox keeps, with the id parameter each one's requestdl takes.</summary>
    private static readonly IReadOnlyDictionary<string, string> Kinds = new Dictionary<string, string>
    {
        ["torrents"] = "torrent_id",
        ["usenet"] = "usenet_id",
        ["webdl"] = "web_id",
    };

    private static readonly string[] GameExtensions = [".nsp", ".nsz", ".xci", ".xcz"];
    private const int PageSize = 1000;
    private const int MaxPages = 50;
    private const long MaxListBytes = 128L * 1024 * 1024;

    internal TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Minimum time between download-link requests. A queue of small files (e.g. a game's 100 DLC)
    /// otherwise asks TorBox for a new link every second or so, which TorBox refuses.
    /// </summary>
    internal TimeSpan LinkRequestSpacing { get; set; } = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _linkGate = new(1, 1);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _lastLinkRequest;

    [GeneratedRegex(@"^torbox:(?<kind>torrents|usenet|webdl)/(?<id>\d{1,18})/(?<file>\d{1,18})(#.*)?$")]
    private static partial Regex ItemUrlRegex();

    public static ServerConnection ConnectionFor(string apiKey) =>
        new(ApiBaseUri, string.Empty, apiKey.Trim(), AllowInsecureHttp: false, ServerKind.TorBox);

    /// <summary>Lists every finished game file (.nsp/.nsz/.xci/.xcz) in the account's torrents, Usenet and web downloads.</summary>
    public async Task<CatalogResult> GetCatalogAsync(ServerConnection connection, CancellationToken cancellationToken)
    {
        RequireKey(connection);
        var items = new List<RemoteGameItem>();
        var warnings = new List<string>();
        foreach (var kind in Kinds.Keys)
        {
            for (var page = 0; ; page++)
            {
                if (page >= MaxPages)
                {
                    warnings.Add($"Only the first {MaxPages * PageSize} TorBox {kind} downloads were read.");
                    break;
                }
                var uri = new Uri(ApiBaseUri,
                    $"{kind}/mylist?bypass_cache=true&offset={page * PageSize}&limit={PageSize}");
                using var doc = await GetJsonAsync(connection, uri, authorize: true, cancellationToken);
                var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
                if (data.ValueKind != JsonValueKind.Array) break;

                var count = 0;
                foreach (var download in data.EnumerateArray())
                {
                    count++;
                    items.AddRange(ReadDownload(kind, download));
                }
                if (count < PageSize) break;
            }
        }

        var unique = items.GroupBy(i => i.Key).Select(g => g.First()).ToList();
        logger.LogInformation("TorBox catalogue retrieved: {Count} game files", unique.Count);
        return new CatalogResult
        {
            Items = unique,
            RetrievedAt = DateTimeOffset.UtcNow,
            Endpoint = "TorBox",
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Asks TorBox for a fresh direct link to the file behind a catalogue item. Links expire, so call this
    /// per attempt. Requests are spaced at least <see cref="LinkRequestSpacing"/> apart, across all downloads.
    /// </summary>
    public async Task<Uri> RequestDownloadLinkAsync(ServerConnection connection, RemoteGameItem item, CancellationToken cancellationToken)
    {
        RequireKey(connection);
        var (kind, downloadId, fileId) = ParseItemUrl(item.Url);
        var query = $"{kind}/requestdl?token={Uri.EscapeDataString(connection.Password)}&{Kinds[kind]}={downloadId}" +
                    $"&file_id={fileId}&zip_link=false";

        JsonDocument doc;
        await _linkGate.WaitAsync(cancellationToken);
        try
        {
            if (_lastLinkRequest is { } last && _clock.Elapsed - last < LinkRequestSpacing)
                await Task.Delay(LinkRequestSpacing - (_clock.Elapsed - last), cancellationToken);
            try
            {
                doc = await GetJsonAsync(connection, new Uri(ApiBaseUri, query), authorize: false, cancellationToken);
            }
            finally
            {
                _lastLinkRequest = _clock.Elapsed;
            }
        }
        finally
        {
            _linkGate.Release();
        }

        using var _ = doc;

        var link = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String ? data.GetString() : null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "TorBox did not return a download link for this file.");
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl, "TorBox returned a download link that is not HTTPS.");
        return uri;
    }

    /// <summary>Checks that an item came from a TorBox catalogue and returns its download kind and ids.</summary>
    public static (string Kind, long DownloadId, long FileId) ParseItemUrl(string? url)
    {
        var match = ItemUrlRegex().Match(url ?? string.Empty);
        if (!match.Success)
            throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl, "This item does not belong to a TorBox catalogue.");
        return (match.Groups["kind"].Value,
            long.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture),
            long.Parse(match.Groups["file"].Value, CultureInfo.InvariantCulture));
    }

    internal static IEnumerable<RemoteGameItem> ReadDownload(string kind, JsonElement download)
    {
        if (download.ValueKind != JsonValueKind.Object) yield break;
        // Only downloads TorBox has finished and stored can be fetched.
        if (!download.TryGetProperty("download_present", out var present) || present.ValueKind != JsonValueKind.True) yield break;
        if (!TryGetLong(download, "id", out var downloadId)) yield break;
        if (!download.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) yield break;

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object || !TryGetLong(file, "id", out var fileId)) continue;
            var fileName = FileNameOf(file);
            if (fileName is null || !GameExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;

            var identity = TitleIdHelper.ParseFileName(fileName);
            yield return new RemoteGameItem
            {
                Name = Path.GetFileNameWithoutExtension(fileName),
                TitleId = TitleIdHelper.BaseTitleIdOf(identity.AppId),
                AppId = identity.AppId,
                AppVersion = (identity.Version ?? 0).ToString(CultureInfo.InvariantCulture),
                ContentType = identity.ContentType,
                Url = $"torbox:{kind}/{downloadId}/{fileId}#{Uri.EscapeDataString(fileName)}",
                Size = TryGetLong(file, "size", out var size) ? size : 0,
                Filename = fileName,
            };
        }
    }

    private static string? FileNameOf(JsonElement file)
    {
        foreach (var property in new[] { "short_name", "name", "s3_path" })
        {
            if (!file.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var name = text.Replace('\\', '/').Split('/').Last().Trim();
            if (name.Length > 0) return name;
        }
        return null;
    }

    private static bool TryGetLong(JsonElement e, string name, out long value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static void RequireKey(ServerConnection connection)
    {
        if (connection.Kind != ServerKind.TorBox || string.IsNullOrWhiteSpace(connection.Password))
            throw new AeroFoilException(AeroFoilErrorKind.NotConfigured, "Enter your TorBox API key (torbox.app → Settings → API key).");
    }

    /// <param name="authorize">Send the key as a Bearer header. requestdl takes it as the documented <c>token</c> query parameter instead.</param>
    private async Task<JsonDocument> GetJsonAsync(ServerConnection connection, Uri uri, bool authorize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (authorize) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Password);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            }
            catch (HttpRequestException ex)
            {
                var mapped = AeroFoilResponses.FromHttpException(ex);
                throw new AeroFoilException(mapped.Kind, mapped.Message.Replace("the server", "TorBox", StringComparison.Ordinal), ex);
            }

            using (response)
            {
                var bytes = await AeroFoilResponses.ReadLimitedAsync(response.Content, MaxListBytes, timeout.Token);
                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(bytes);
                }
                catch (JsonException)
                {
                }

                var detail = doc is null ? null : ErrorDetail(doc.RootElement);
                var failed = !response.IsSuccessStatusCode || doc is null ||
                             (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False);
                if (!failed) return doc!;

                doc?.Dispose();
                throw Classify(response.StatusCode, detail).WithStatus((int)response.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AeroFoilException(AeroFoilErrorKind.Timeout, "TorBox did not respond in time.");
        }
    }

    private static string? ErrorDetail(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "detail", "error" })
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        return null;
    }

    internal static AeroFoilException Classify(HttpStatusCode status, string? detail)
    {
        var code = (int)status;
        var text = detail ?? string.Empty;
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
            text.Contains("token", StringComparison.OrdinalIgnoreCase) || text.Contains("authenticat", StringComparison.OrdinalIgnoreCase))
            return new AeroFoilException(AeroFoilErrorKind.InvalidCredentials,
                "TorBox rejected the API key. Copy it again from torbox.app → Settings, and check your plan is active.");
        if (status == HttpStatusCode.TooManyRequests)
            return new AeroFoilException(AeroFoilErrorKind.RateLimited, "TorBox is rate limiting requests. Wait a minute and try again.");
        if (status is HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout)
            return new AeroFoilException(AeroFoilErrorKind.Timeout, $"TorBox timed out (HTTP {code}).");
        if (code >= 500)
            return new AeroFoilException(AeroFoilErrorKind.ServerError, $"TorBox reported an error (HTTP {code}).");
        if (status == HttpStatusCode.NotFound)
            return new AeroFoilException(AeroFoilErrorKind.NotFound, "TorBox no longer has this download. It may have expired or been deleted.");
        return new AeroFoilException(AeroFoilErrorKind.InvalidResponse,
            string.IsNullOrWhiteSpace(detail) ? $"Unexpected TorBox response (HTTP {code})." : $"TorBox (HTTP {code}): {detail}");
    }
}
