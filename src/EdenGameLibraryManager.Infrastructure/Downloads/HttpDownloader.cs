using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Downloads;

public sealed record DownloadRequest(
    ServerConnection Connection,
    Uri Uri,
    string PartialPath,
    long ExpectedSize,
    string? ETag,
    string? LastModified);

/// <param name="SizeVerified">True when the server announced the total size and exactly that many bytes were stored.</param>
public sealed record DownloadResult(long TotalBytes, string? ETag, string? LastModified, bool Resumed, bool SizeVerified);

/// <summary>
/// Streams one file to a ".partial" path, resuming with HTTP Range when it is safe:
/// the stored ETag (or Last-Modified) is sent as If-Range, and a resumed response must be
/// 206 with a Content-Range starting exactly at the existing length. Anything else restarts
/// the file from zero rather than risk splicing two different versions together.
/// </summary>
public sealed class HttpDownloader(IAeroFoilClient client, BandwidthLimiter limiter, ILogger<HttpDownloader> logger)
{
    private const int BufferSize = 256 * 1024;

    internal TimeSpan ReadInactivityTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <param name="onStarted">Called once response headers are validated, with the validators to persist and the total size.</param>
    /// <param name="onProgress">Called with the number of bytes now in the partial file.</param>
    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        Action<string?, string?, long?> onStarted,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var existing = File.Exists(request.PartialPath) ? new FileInfo(request.PartialPath).Length : 0;
            var etag = IsStrongETag(request.ETag) ? request.ETag : null;
            var lastModified = ParseHttpDate(request.LastModified);
            var tryResume = attempt == 0 && existing > 0 && (etag is not null || lastModified is not null) &&
                            (request.ExpectedSize <= 0 || existing <= request.ExpectedSize);

            if (!tryResume && existing > 0)
            {
                logger.LogInformation("Discarding {Bytes} bytes of partial data for {Path}: resume is not safe", existing, request.PartialPath);
                File.Delete(request.PartialPath);
                existing = 0;
            }

            using var message = new HttpRequestMessage(HttpMethod.Get, request.Uri);
            if (tryResume)
            {
                message.Headers.Range = new RangeHeaderValue(existing, null);
                message.Headers.IfRange = etag is not null
                    ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(etag))
                    : new RangeConditionHeaderValue(lastModified!.Value);
            }

            using var response = await client.SendAsync(request.Connection, message, cancellationToken);
            await EnsureFileResponseAsync(response, cancellationToken);

            long offset;
            long? total;
            var range = response.Content.Headers.ContentRange;
            switch (response.StatusCode)
            {
                case HttpStatusCode.PartialContent:
                    if (!tryResume || range is not { HasRange: true, Length: not null } || range.From != existing)
                    {
                        logger.LogWarning("Unexpected Content-Range '{Range}' when resuming at {Offset}; restarting", range, existing);
                        continue;
                    }
                    var responseTag = response.Headers.ETag?.ToString();
                    if (etag is not null && responseTag is not null && responseTag != etag)
                    {
                        logger.LogWarning("Remote file changed (ETag mismatch); restarting download");
                        continue;
                    }
                    offset = existing;
                    total = range.Length;
                    break;

                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    if (tryResume && range?.Length == existing)
                    {
                        // The partial file already holds every byte.
                        onStarted(request.ETag, request.LastModified, existing);
                        onProgress(existing);
                        return new DownloadResult(existing, request.ETag, request.LastModified, true, true);
                    }
                    logger.LogWarning("Server rejected the resume range; restarting download");
                    continue;

                case HttpStatusCode.OK:
                    if (existing > 0)
                        logger.LogInformation("Server returned the full file (range unsupported or file changed); restarting from zero");
                    offset = 0;
                    total = response.Content.Headers.ContentLength;
                    break;

                default:
                    throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, $"Unexpected download response (HTTP {(int)response.StatusCode}).");
            }

            var newETag = response.Headers.ETag?.ToString() ?? (offset > 0 ? request.ETag : null);
            var newLastModified = response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture)
                                  ?? (offset > 0 ? request.LastModified : null);

            if (request.ExpectedSize > 0 && total is { } t && t != request.ExpectedSize)
                logger.LogWarning("Server size {ServerSize} differs from catalogue size {CatalogueSize}; using server size", t, request.ExpectedSize);

            onStarted(newETag, newLastModified, total);
            var written = await CopyToFileAsync(response, request.PartialPath, offset, onProgress, cancellationToken);

            if (total is { } expectedTotal && written != expectedTotal)
                throw new IncompleteDownloadException($"Transfer ended early: received {written:N0} of {expectedTotal:N0} bytes.");

            return new DownloadResult(written, newETag, newLastModified, offset > 0, total is not null);
        }

        throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse, "The server's responses were inconsistent, so the download could not be restarted safely.");
    }

    private async Task<long> CopyToFileAsync(
        HttpResponseMessage response, string path, long offset, Action<long> onProgress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = new FileStream(path, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.Read, BufferSize, FileOptions.Asynchronous);

        if (file.Position != offset)
            throw new IncompleteDownloadException("The partial file changed while resuming.");

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var written = offset;
        onProgress(written);
        try
        {
            while (true)
            {
                readTimeout.CancelAfter(ReadInactivityTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), readTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new AeroFoilException(AeroFoilErrorKind.Timeout,
                        $"The download stalled: no data received for {ReadInactivityTimeout.TotalSeconds:0} seconds.");
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw new AeroFoilException(AeroFoilErrorKind.Network, "The connection was lost during the download.", ex);
                }

                if (read == 0) break;
                await limiter.WaitAsync(read, cancellationToken);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                onProgress(written);
            }
            await file.FlushAsync(cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return written;
    }

    private static async Task EnsureFileResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable))
            await AeroFoilResponses.ThrowForStatusAsync(response, ct);

        // AeroFoil answers failed authentication on /api/get_game with HTTP 200 and a JSON error body.
        if (AeroFoilResponses.IsJson(response))
            throw await AeroFoilResponses.ReadUnexpectedJsonAsync(response, "the game file", ct);
        if (AeroFoilResponses.IsHtml(response))
            throw new AeroFoilException(AeroFoilErrorKind.InvalidResponse,
                "The server returned a web page instead of the game file (possibly a login or proxy page).");
    }

    private static bool IsStrongETag(string? etag) =>
        !string.IsNullOrWhiteSpace(etag) && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) &&
        EntityTagHeaderValue.TryParse(etag, out _);

    private static DateTimeOffset? ParseHttpDate(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
