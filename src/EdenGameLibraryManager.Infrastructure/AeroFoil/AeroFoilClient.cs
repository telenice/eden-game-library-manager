using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.AeroFoil;

/// <summary>
/// HTTP client for the AeroFoil Remote API. Endpoints were verified against the AeroFoil source
/// (app/app.py) and the CyberFoil remote protocol documentation:
/// <list type="bullet">
/// <item><c>GET /api/remote/sections</c> (CyberFoil "modern" path, tried first) then <c>GET /api/shop/sections</c>.</item>
/// <item><c>GET /api/shop/title-info/{title_id}</c> for description and screenshot URLs.</item>
/// <item>Item <c>url</c> / <c>icon_url</c> values (e.g. <c>/api/get_game/{id}#file.nsp</c>) are used as given.</item>
/// </list>
/// Credentials are attached per request, only to the configured origin, and redirects are followed
/// manually so they can never be forwarded to another host.
/// </summary>
public sealed class AeroFoilClient(HttpClient http, ILogger<AeroFoilClient> logger) : IAeroFoilClient
{
    public const string RemoteSectionsPath = "api/remote/sections";
    public const string ShopSectionsPath = "api/shop/sections";
    public const string TitleInfoPath = "api/shop/title-info/";

    private const long MaxCatalogBytes = 256L * 1024 * 1024;
    private const int MaxRedirects = 5;

    internal TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Handler used by the app: no automatic redirects or decompression, default certificate validation.</summary>
    public static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        UseCookies = false,
    };

    public async Task<CatalogResult> GetCatalogAsync(ServerConnection connection, CancellationToken cancellationToken)
    {
        var (root, endpoint) = await FetchSectionsAsync(connection, null, null, cancellationToken);
        var parsed = CatalogParser.Parse(root);

        if (parsed.AllTruncated && parsed.AllTotal is { } total && total > parsed.AllCount)
        {
            // AeroFoil caps the "all" section at max(limit, cap) for non-CyberFoil clients when the
            // operator configured SHOP_SECTIONS_ALL_ITEMS_CAP; the "limit" query parameter read by
            // shop_sections_api raises that cap.
            logger.LogInformation("Catalogue 'all' section truncated ({Count}/{Total}); re-requesting with limit", parsed.AllCount, total);
            (root, endpoint) = await FetchSectionsAsync(connection, endpoint, total, cancellationToken);
            parsed = CatalogParser.Parse(root);
        }

        if (parsed.AccountFrozen && parsed.Items.Count == 0)
            throw new AeroFoilException(AeroFoilErrorKind.AccountFrozen, "Your AeroFoil account is frozen. Contact the server administrator.");

        var warnings = new List<string>();
        var complete = true;
        if (!parsed.HasAllSection)
        {
            complete = false;
            warnings.Add("The server did not return a full-library section, so the catalogue may be incomplete.");
        }
        else if (parsed.AllTruncated)
        {
            complete = false;
            warnings.Add($"The server limited the catalogue to {parsed.AllCount} of {parsed.AllTotal?.ToString() ?? "?"} items.");
        }
        if (parsed.SkippedItems > 0)
            warnings.Add($"{parsed.SkippedItems} catalogue entries were ignored because they had no download URL.");

        logger.LogInformation("Catalogue retrieved from {Endpoint}: {Count} unique items (complete: {Complete})", endpoint, parsed.Items.Count, complete);

        return new CatalogResult
        {
            Items = parsed.Items,
            RetrievedAt = DateTimeOffset.UtcNow,
            Endpoint = "/" + endpoint,
            IsComplete = complete,
            Warnings = warnings,
        };
    }

    public async Task<TitleDetails?> GetTitleInfoAsync(ServerConnection connection, string titleId, CancellationToken cancellationToken)
    {
        if (!TitleIdHelper.IsValidId(titleId)) return null;
        var uri = UrlPolicy.Combine(connection.BaseUri, TitleInfoPath + titleId.ToUpperInvariant());

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            using var response = await SendAsync(connection, request, timeout.Token);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.ServiceUnavailable or HttpStatusCode.Forbidden)
                return null;
            await AeroFoilResponses.ThrowForStatusAsync(response, timeout.Token);
            if (!AeroFoilResponses.IsJson(response)) return null;

            var bytes = await AeroFoilResponses.ReadLimitedAsync(response.Content, 4 * 1024 * 1024, timeout.Token);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            AeroFoilResponses.ThrowIfErrorPayload(root, "success");
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;

            var screenshots = new List<string>();
            if (root.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
                screenshots.AddRange(shots.EnumerateArray()
                    .Where(s => s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                    .Select(s => s.GetString()!));

            return new TitleDetails
            {
                TitleId = titleId.ToUpperInvariant(),
                Description = CatalogParser.GetString(root, "description"),
                ScreenshotUrls = screenshots,
            };
        }
        catch (JsonException)
        {
            logger.LogWarning("Malformed title-info response for {TitleId}", titleId);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AeroFoilException(AeroFoilErrorKind.Timeout, "The server did not respond in time.");
        }
    }

    public async Task<byte[]> GetResourceAsync(ServerConnection connection, string url, long maxBytes, CancellationToken cancellationToken)
    {
        var uri = UrlPolicy.ResolveTrusted(connection.BaseUri, url);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var timeout = CreateTimeout(cancellationToken);
        try
        {
            using var response = await SendAsync(connection, request, timeout.Token);
            await AeroFoilResponses.ThrowForStatusAsync(response, timeout.Token);
            if (AeroFoilResponses.IsJson(response))
                throw await AeroFoilResponses.ReadUnexpectedJsonAsync(response, "an image", timeout.Token);
            return await AeroFoilResponses.ReadLimitedAsync(response.Content, maxBytes, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AeroFoilException(AeroFoilErrorKind.Timeout, "The server did not respond in time.");
        }
    }

    public async Task<HttpResponseMessage> SendAsync(ServerConnection connection, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        try
        {
            for (var hop = 0; ; hop++)
            {
                var uri = current.RequestUri ?? throw new ArgumentException("Request URI is required.", nameof(request));
                if (!UrlPolicy.IsSameOrigin(uri, connection.BaseUri))
                    throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl,
                        $"Refusing to contact {uri.Scheme}://{uri.Authority}: it is not the configured AeroFoil server.");
                if (uri.Scheme == Uri.UriSchemeHttp && !connection.AllowInsecureHttp)
                    throw new AeroFoilException(AeroFoilErrorKind.InsecureConnection, "Plain HTTP is disabled for this server.");

                current.Headers.Authorization = connection.HasCredentials
                    ? new AuthenticationHeaderValue("Basic", connection.BasicAuthParameter)
                    : null;

                HttpResponseMessage response;
                try
                {
                    response = await http.SendAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException ex)
                {
                    throw AeroFoilResponses.FromHttpException(ex);
                }

                if (!IsRedirect(response.StatusCode) || response.Headers.Location is null)
                    return response;

                var target = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(uri, response.Headers.Location);
                var status = response.StatusCode;
                response.Dispose();

                if (hop >= MaxRedirects)
                    throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "The server redirected too many times.");
                if (!UrlPolicy.IsSameOrigin(target, connection.BaseUri))
                    throw new AeroFoilException(AeroFoilErrorKind.UntrustedUrl,
                        $"The server redirected to {target.Scheme}://{target.Authority}, which is not the configured server. " +
                        "The request was stopped and your credentials were not sent there.");

                logger.LogDebug("Following same-origin redirect ({Status}) to {Path}", (int)status, target.AbsolutePath);
                var next = CloneForRedirect(current, target, status);
                if (!ReferenceEquals(current, request)) current.Dispose();
                current = next;
            }
        }
        finally
        {
            if (!ReferenceEquals(current, request)) current.Dispose();
        }
    }

    private async Task<(JsonElement Root, string Endpoint)> FetchSectionsAsync(
        ServerConnection connection, string? knownEndpoint, int? limit, CancellationToken cancellationToken)
    {
        string[] paths = knownEndpoint is not null ? [knownEndpoint] : [RemoteSectionsPath, ShopSectionsPath];
        foreach (var path in paths)
        {
            var relative = limit is { } l ? $"{path}?limit={l}" : path;
            var uri = UrlPolicy.Combine(connection.BaseUri, relative);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeout = CreateTimeout(cancellationToken);
            try
            {
                using var response = await SendAsync(connection, request, timeout.Token);
                var isFallbackCandidate = path == RemoteSectionsPath && knownEndpoint is null;
                if (isFallbackCandidate && (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                                            || (response.IsSuccessStatusCode && AeroFoilResponses.IsHtml(response))))
                {
                    logger.LogInformation("{Path} unavailable (HTTP {Status}); falling back to /{Fallback}", path, (int)response.StatusCode, ShopSectionsPath);
                    continue;
                }

                await AeroFoilResponses.ThrowForStatusAsync(response, timeout.Token);
                var bytes = await AeroFoilResponses.ReadLimitedAsync(response.Content, MaxCatalogBytes, timeout.Token);

                var media = response.Content.Headers.ContentType?.MediaType;
                if (StartsWithAscii(bytes, "TINFOIL") || string.Equals(media, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
                    throw new AeroFoilException(AeroFoilErrorKind.EncryptedCatalog, AeroFoilResponses.EncryptedMessage);

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(bytes);
                }
                catch (JsonException)
                {
                    throw AeroFoilResponses.IsHtml(response)
                        ? new AeroFoilException(AeroFoilErrorKind.InvalidResponse,
                            "The server returned a web page instead of the catalogue. Check the server URL.")
                        : new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "The server returned an invalid catalogue.");
                }

                var root = doc.RootElement.Clone();
                doc.Dispose();
                AeroFoilResponses.ThrowIfErrorPayload(root, "sections");
                return (root, path);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AeroFoilException(AeroFoilErrorKind.Timeout, "The server did not respond in time.");
            }
        }
        throw new AeroFoilException(AeroFoilErrorKind.NotFound, "The server does not expose an AeroFoil catalogue API.");
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ApiTimeout);
        return cts;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage CloneForRedirect(HttpRequestMessage source, Uri target, HttpStatusCode status)
    {
        var method = status == HttpStatusCode.SeeOther ? HttpMethod.Get : source.Method;
        var clone = new HttpRequestMessage(method, target) { Version = source.Version };
        foreach (var header in source.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }

    private static bool StartsWithAscii(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;
        return true;
    }
}
