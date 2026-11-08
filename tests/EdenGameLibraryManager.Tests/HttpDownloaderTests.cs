using System.Net;
using System.Net.Http.Headers;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

public class HttpDownloaderTests
{
    private static readonly Uri FileUri = new("https://aerofoil.example.com/api/get_game/5");
    private static readonly byte[] Content = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();
    private const string ETag = "\"1700000000.0-100000-42\"";

    private static HttpDownloader Downloader(FakeHandler handler) =>
        new(TestData.Client(handler), new BandwidthLimiter(), NullLogger<HttpDownloader>.Instance);

    /// <summary>A server that honours Range/If-Range like Werkzeug's send_file (used by AeroFoil).</summary>
    private static HttpResponseMessage RangeServer(HttpRequestMessage request, byte[] content, string etag)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var ifRange = request.Headers.IfRange?.EntityTag?.ToString();
        if (range is not null && (ifRange is null || ifRange == etag))
        {
            var from = range.From!.Value;
            if (from >= content.Length)
            {
                var r416 = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
                r416.Content.Headers.ContentRange = new ContentRangeHeaderValue(content.Length);
                return r416;
            }
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(content[(int)from..]) };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            partial.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            partial.Headers.ETag = EntityTagHeaderValue.Parse(etag);
            return partial;
        }
        var full = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        full.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        full.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        full.Headers.AcceptRanges.Add("bytes");
        return full;
    }

    [Fact]
    public async Task Downloads_full_file_and_reports_validators()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        var handler = new FakeHandler(r => RangeServer(r, Content, ETag));
        string? storedTag = null;
        long lastProgress = 0;

        var result = await Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, Content.Length, null, null),
            (tag, _, _) => storedTag = tag, b => lastProgress = b, default);

        Assert.Equal(Content, await File.ReadAllBytesAsync(path));
        Assert.True(result.SizeVerified);
        Assert.False(result.Resumed);
        Assert.Equal(ETag, storedTag);
        Assert.Equal(Content.Length, lastProgress);
        Assert.Null(handler.Requests[0].Headers.Range);
    }

    [Fact]
    public async Task Resumes_interrupted_download_with_range_and_if_range()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        await File.WriteAllBytesAsync(path, Content[..40_000]);
        var handler = new FakeHandler(r => RangeServer(r, Content, ETag));

        var result = await Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, Content.Length, ETag, null), (_, _, _) => { }, _ => { }, default);

        Assert.True(result.Resumed);
        Assert.Equal(Content, await File.ReadAllBytesAsync(path));
        var request = handler.Requests.Single();
        Assert.Equal(40_000, request.Headers.Range!.Ranges.Single().From);
        Assert.Equal(ETag, request.Headers.IfRange!.EntityTag!.ToString());
    }

    [Fact]
    public async Task Restarts_when_remote_file_changed()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        await File.WriteAllBytesAsync(path, new byte[40_000]); // stale bytes from the old version
        var changed = Content.Reverse().ToArray();
        var handler = new FakeHandler(r => RangeServer(r, changed, "\"new-version\""));

        var result = await Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, changed.Length, ETag, null), (_, _, _) => { }, _ => { }, default);

        Assert.False(result.Resumed);
        Assert.Equal(changed, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Does_not_resume_without_a_validator()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        await File.WriteAllBytesAsync(path, Content[..40_000]);
        var handler = new FakeHandler(r => RangeServer(r, Content, ETag));

        await Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, Content.Length, null, null), (_, _, _) => { }, _ => { }, default);

        Assert.Null(handler.Requests.Single().Headers.Range);
        Assert.Equal(Content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Restarts_when_content_range_does_not_match_partial_length()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        await File.WriteAllBytesAsync(path, Content[..40_000]);
        var calls = 0;
        var handler = new FakeHandler(r =>
        {
            if (calls++ == 0)
            {
                // Misbehaving server: answers with a range starting at the wrong offset.
                var bad = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Content[10..]) };
                bad.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, Content.Length - 1, Content.Length);
                return bad;
            }
            return RangeServer(r, Content, ETag);
        });

        var result = await Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, Content.Length, ETag, null), (_, _, _) => { }, _ => { }, default);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(handler.Requests[1].Headers.Range);
        Assert.False(result.Resumed);
        Assert.Equal(Content, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Already_complete_partial_is_accepted_on_416()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        await File.WriteAllBytesAsync(path, Content);
        var handler = new FakeHandler(r => RangeServer(r, Content, ETag));

        var result = await Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, Content.Length, ETag, null), (_, _, _) => { }, _ => { }, default);

        Assert.Equal(Content.Length, result.TotalBytes);
        Assert.True(result.SizeVerified);
    }

    [Fact]
    public async Task Short_transfer_raises_incomplete_download()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        var handler = new FakeHandler(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content[..500]) };
            r.Content.Headers.ContentLength = Content.Length; // promised more than delivered
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return r;
        });

        await Assert.ThrowsAsync<IncompleteDownloadException>(() => Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, Content.Length, null, null), (_, _, _) => { }, _ => { }, default));
        Assert.True(File.Exists(path)); // kept for resuming
    }

    [Fact]
    public async Task Json_error_instead_of_file_is_an_auth_failure_not_a_game()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("a.partial");
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"error": "Incorrect password for user \"alice\"."}"""));

        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, path, Content.Length, null, null), (_, _, _) => { }, _ => { }, default));

        Assert.Equal(AeroFoilErrorKind.InvalidCredentials, ex.Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Restricted_download_is_access_denied()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => Downloader(handler).DownloadAsync(
            new DownloadRequest(TestData.Connection, FileUri, temp.Combine("a.partial"), 1, null, null), (_, _, _) => { }, _ => { }, default));
        Assert.Equal(AeroFoilErrorKind.AccessDenied, ex.Kind);
    }
}
