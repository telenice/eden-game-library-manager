using System.Net;
using System.Text;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

/// <summary>Programmable fake HTTP server: each request is answered by the supplied handler.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class TestData
{
    public static readonly ServerConnection Connection =
        new(new Uri("https://aerofoil.example.com/"), "alice", "s3cret", AllowInsecureHttp: false);

    public static AeroFoilClient Client(FakeHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, NullLogger<AeroFoilClient>.Instance);

    public static RemoteGameItem Item(
        string name, string appId, ContentType type, string version = "0", long size = 1000, long? fileId = 1, string? filename = null) => new()
    {
        Name = name,
        TitleId = appId,
        AppId = appId,
        AppVersion = version,
        ContentType = type,
        Size = size,
        FileId = fileId,
        Filename = filename ?? $"{name} [{appId}][v{version}].nsp",
        Url = $"/api/get_game/{fileId}#{Uri.EscapeDataString(filename ?? $"{name} [{appId}][v{version}].nsp")}",
    };

    public static LocalGame Local(string path, long size, bool managed = true, string? appId = null, ContentType type = ContentType.Base,
        long? version = 0, bool pinned = false, DateTimeOffset? lastUsed = null, long id = 0) => new()
    {
        Id = id,
        LocalPath = path,
        ActualSize = size,
        ExpectedSize = managed ? size : null,
        IsManaged = managed,
        AppId = appId,
        ContentType = type,
        Version = version,
        IsPinned = pinned,
        FirstSeenAt = lastUsed ?? DateTimeOffset.UtcNow,
        DownloadedAt = managed ? lastUsed ?? DateTimeOffset.UtcNow : null,
        Status = LocalGameStatus.Complete,
    };
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "egl-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class StaticConnectionProvider(ServerConnection? connection) : IConnectionProvider
{
    public ServerConnection? Connection { get; set; } = connection;
    public ServerConnection? GetConnection() => Connection;
}

internal sealed class InMemorySettings(AppSettings settings) : ISettingsService
{
    public AppSettings Current { get; private set; } = settings;
    public event EventHandler<AppSettings>? Changed;

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Changed?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}
