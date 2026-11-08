using System.Net.Http.Headers;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using EdenGameLibraryManager.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

/// <summary>
/// Optional tests against a real AeroFoil server. They are skipped unless these environment
/// variables are set:
///   EGL_TEST_SERVER    e.g. https://aerofoil.example.com   (http:// is allowed for a local test server)
///   EGL_TEST_USER / EGL_TEST_PASSWORD
///   EGL_TEST_DOWNLOAD=1  also downloads the smallest catalogue item to a temp folder (and deletes it)
/// </summary>
public class IntegrationTests
{
    private static ServerConnection? Connection()
    {
        var server = Environment.GetEnvironmentVariable("EGL_TEST_SERVER");
        if (string.IsNullOrWhiteSpace(server)) return null;
        var uri = UrlPolicy.NormalizeServerUrl(server, allowInsecureHttp: true);
        return new ServerConnection(uri,
            Environment.GetEnvironmentVariable("EGL_TEST_USER") ?? string.Empty,
            Environment.GetEnvironmentVariable("EGL_TEST_PASSWORD") ?? string.Empty,
            AllowInsecureHttp: uri.Scheme == Uri.UriSchemeHttp);
    }

    private static AeroFoilClient RealClient()
    {
        var http = new HttpClient(AeroFoilClient.CreateDefaultHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EdenGameLibraryManager", "1.0.0-test"));
        return new AeroFoilClient(http, NullLogger<AeroFoilClient>.Instance);
    }

    [IntegrationFact]
    public async Task Retrieves_catalogue_from_configured_server()
    {
        var catalog = await RealClient().GetCatalogAsync(Connection()!, default);
        Assert.NotNull(catalog.Endpoint);
        Assert.All(catalog.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Url)));
    }

    [IntegrationFact("EGL_TEST_DOWNLOAD")]
    public async Task Downloads_and_resumes_smallest_item()
    {
        var connection = Connection()!;
        var client = RealClient();
        var catalog = await client.GetCatalogAsync(connection, default);
        var item = catalog.Items.Where(i => i.Size > 0).OrderBy(i => i.Size).First();
        using var temp = new TempDirectory();
        var path = temp.Combine("item.partial");
        var downloader = new HttpDownloader(client, new BandwidthLimiter(), NullLogger<HttpDownloader>.Instance);
        var uri = UrlPolicy.ResolveTrusted(connection.BaseUri, item.Url);

        // Download the first part, then cancel, then resume.
        using var cts = new CancellationTokenSource();
        string? etag = null, lastModified = null;
        try
        {
            await downloader.DownloadAsync(new DownloadRequest(connection, uri, path, item.Size, null, null),
                (e, lm, _) => { etag = e; lastModified = lm; },
                bytes => { if (bytes > Math.Min(item.Size / 2, 4 * 1024 * 1024)) cts.Cancel(); }, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        var result = await downloader.DownloadAsync(new DownloadRequest(connection, uri, path, item.Size, etag, lastModified),
            (_, _, _) => { }, _ => { }, default);
        Assert.Equal(item.Size, new FileInfo(path).Length);
        Assert.Equal(item.Size, result.TotalBytes);
    }
}

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute(string? extraVariable = null)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EGL_TEST_SERVER")))
            Skip = "Set EGL_TEST_SERVER (and EGL_TEST_USER / EGL_TEST_PASSWORD) to run integration tests.";
        else if (extraVariable is not null && Environment.GetEnvironmentVariable(extraVariable) != "1")
            Skip = $"Set {extraVariable}=1 to run this integration test.";
    }
}
