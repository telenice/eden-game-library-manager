using System.Net;
using System.Net.Http.Headers;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using EdenGameLibraryManager.Infrastructure.Downloads;
using EdenGameLibraryManager.Infrastructure.Persistence;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

public partial class DownloadManagerTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();

    private sealed record Harness(DownloadManager Manager, LocalLibraryService Library, ManifestService Manifest,
        InMemorySettings Settings, AppPaths Paths, FakeHandler Handler, StaticConnectionProvider Connections);

    private static async Task<Harness> CreateAsync(TempDirectory temp, Func<HttpRequestMessage, HttpResponseMessage> server,
        AppSettings? settings = null, Func<ManifestService, AppPaths, Task>? seed = null)
    {
        var paths = new AppPaths(temp.Combine("appdata"));
        paths.EnsureCreated();
        var games = temp.Combine("games");
        var s = new InMemorySettings(settings ?? new AppSettings { GamesDirectory = games });
        var manifest = new ManifestService(paths.DatabaseFile, NullLogger<ManifestService>.Instance);
        await manifest.InitializeAsync(default);
        if (seed is not null) await seed(manifest, paths);
        var library = new LocalLibraryService(manifest, s, NullLogger<LocalLibraryService>.Instance);
        var cache = new CacheManager(library, s, NullLogger<CacheManager>.Instance);
        var handler = new FakeHandler(server);
        var downloader = new HttpDownloader(TestData.Client(handler), new BandwidthLimiter(), NullLogger<HttpDownloader>.Instance);
        var connections = new StaticConnectionProvider(TestData.Connection);
        var manager = new DownloadManager(downloader, TestData.Links(handler), connections, manifest, cache, library, s, paths, new BandwidthLimiter(),
            NullLogger<DownloadManager>.Instance)
        {
            Backoff = _ => TimeSpan.FromMilliseconds(10),
        };
        await manager.InitializeAsync(default);
        return new Harness(manager, library, manifest, s, paths, handler, connections);
    }

    private static HttpResponseMessage FileResponse(byte[] data)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        r.Headers.ETag = EntityTagHeaderValue.Parse("\"v1\"");
        return r;
    }

    private static async Task<DownloadTask> WaitForAsync(DownloadManager manager, string id, Func<DownloadStatus, bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var task = manager.Tasks.First(t => t.Id == id);
            if (predicate(task.Status)) return task;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Download did not reach the expected state; last: {manager.Tasks.First(t => t.Id == id).Status}");
    }

    [Fact]
    public async Task Completed_download_is_moved_into_games_directory_and_registered()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FileResponse(Payload));
        var item = TestData.Item("Mario Party Superstars", "01006FE013472000", ContentType.Base, size: Payload.Length, fileId: 12);
        DownloadTask? completed = null;
        h.Manager.TaskCompleted += (_, t) => completed = t;

        var task = h.Manager.Enqueue(item);
        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.NotNull(completed);
        // Organised by game by default: "<games>/<name> [TitleID]/<file>".
        var finalPath = Path.Combine(h.Settings.Current.GamesDirectory, "Mario Party Superstars [01006FE013472000]", item.SourceFileName!);
        Assert.Equal(finalPath, task.CompletedPath);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(finalPath));
        Assert.False(File.Exists(task.PartialPath));
        Assert.Empty(Directory.EnumerateFiles(h.Paths.DownloadsDirectory));

        var local = Assert.Single(h.Library.Games);
        Assert.True(local.IsManaged);
        Assert.Equal(12, local.RemoteFileId);
        Assert.Equal(EdenGameLibraryManager.Core.Services.MatchState.Downloaded,
            EdenGameLibraryManager.Core.Services.LibraryMatcher.Match(item, h.Library.Games).State);
        Assert.Empty(await h.Manifest.GetDownloadsAsync(default));
    }

    [Fact]
    public async Task Existing_file_with_same_name_is_not_overwritten()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FileResponse(Payload),
            new AppSettings { GamesDirectory = temp.Combine("games"), OrganizeByGame = false });
        var item = TestData.Item("Game", "0100000000010000", ContentType.Base, size: Payload.Length, filename: "Game.nsp");
        Directory.CreateDirectory(h.Settings.Current.GamesDirectory);
        var existing = Path.Combine(h.Settings.Current.GamesDirectory, "Game.nsp");
        await File.WriteAllTextAsync(existing, "user file");

        var task = h.Manager.Enqueue(item);
        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());

        Assert.Equal("user file", await File.ReadAllTextAsync(existing));
        Assert.EndsWith("Game (1).nsp", task.CompletedPath);
    }

    [Fact]
    public async Task Base_update_and_dlc_share_one_game_folder()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FileResponse(Payload));
        var baseGame = TestData.Item("Mario Kart 8 Deluxe", "0100152000022000", ContentType.Base, size: Payload.Length, fileId: 1);
        var update = TestData.Item("Mario Kart 8 Deluxe", "0100152000022800", ContentType.Update, version: "196608", size: Payload.Length, fileId: 2)
            with { TitleId = "0100152000022000", TitleName = "Mario Kart 8 Deluxe" };
        var dlc = TestData.Item("Booster Course Pass", "0100152000023001", ContentType.Dlc, size: Payload.Length, fileId: 3)
            with { TitleId = "0100152000022000", TitleName = "Mario Kart 8 Deluxe" };

        var tasks = new[] { baseGame, update, dlc }.Select(h.Manager.Enqueue).ToList();
        foreach (var t in tasks) await WaitForAsync(h.Manager, t.Id, s => s.IsTerminal());

        var folder = Path.Combine(h.Settings.Current.GamesDirectory, "Mario Kart 8 Deluxe [0100152000022000]");
        Assert.All(tasks, t => Assert.Equal(folder, Path.GetDirectoryName(t.CompletedPath)));
        Assert.Equal(3, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task Flat_layout_is_used_when_organising_is_off()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FileResponse(Payload),
            new AppSettings { GamesDirectory = temp.Combine("games"), OrganizeByGame = false });
        var task = h.Manager.Enqueue(TestData.Item("Game", "0100000000010000", ContentType.Base, size: Payload.Length, filename: "Game.nsp"));
        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());
        Assert.Equal(Path.Combine(h.Settings.Current.GamesDirectory, "Game.nsp"), task.CompletedPath);
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff()
    {
        using var temp = new TempDirectory();
        var calls = 0;
        var h = await CreateAsync(temp, _ => ++calls < 3 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : FileResponse(Payload));
        var task = h.Manager.Enqueue(TestData.Item("Game", "0100000000010000", ContentType.Base, size: Payload.Length));

        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Invalid_credentials_are_not_retried()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FakeHandler.Json("""{"error": "Incorrect password for user \"alice\"."}"""));
        var task = h.Manager.Enqueue(TestData.Item("Game", "0100000000010000", ContentType.Base, size: Payload.Length));

        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());
        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("Sign-in failed", task.Message);
        Assert.Single(h.Handler.Requests);
    }

    [Fact]
    public async Task Untrusted_catalogue_url_is_rejected_before_any_request()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FileResponse(Payload));
        var evil = TestData.Item("Evil", "0100000000010000", ContentType.Base) with { Url = "https://evil.example.net/x.nsp" };

        var ex = Assert.Throws<AeroFoilException>(() => h.Manager.Enqueue(evil));
        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, ex.Kind);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task Unsafe_filename_is_rejected()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FileResponse(Payload));
        var evil = TestData.Item("Evil", "0100000000010000", ContentType.Base) with { Filename = "..\\..\\evil.nsp" };
        Assert.Throws<UnsafePathException>(() => h.Manager.Enqueue(evil));
    }

    [Fact]
    public async Task Cache_limit_in_manual_mode_fails_the_download()
    {
        using var temp = new TempDirectory();
        var settings = new AppSettings { GamesDirectory = temp.Combine("games"), CacheLimitGb = 1.0 / 1024 / 1024 }; // 1 KB
        var h = await CreateAsync(temp, _ => FileResponse(Payload), settings);
        var task = h.Manager.Enqueue(TestData.Item("Game", "0100000000010000", ContentType.Base, size: Payload.Length));

        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());
        Assert.Equal(DownloadStatus.Failed, task.Status);
        Assert.Contains("cache", task.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task Cancel_removes_partial_data()
    {
        using var temp = new TempDirectory();
        var gate = new TaskCompletionSource();
        var h = await CreateAsync(temp, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(Payload[..1000], gate.Task)) };
            r.Content.Headers.ContentLength = Payload.Length;
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return r;
        });
        var task = h.Manager.Enqueue(TestData.Item("Game", "0100000000010000", ContentType.Base, size: Payload.Length));
        await WaitForAsync(h.Manager, task.Id, s => s == DownloadStatus.Downloading);
        while (task.DownloadedBytes < 1000) await Task.Delay(10);

        await h.Manager.CancelAsync(task.Id);

        Assert.Equal(DownloadStatus.Cancelled, task.Status);
        Assert.False(File.Exists(task.PartialPath));
        Assert.Empty(await h.Manifest.GetDownloadsAsync(default));
        gate.SetResult();
    }

    [Fact]
    public async Task Paused_download_is_persisted_and_restored_after_restart()
    {
        using var temp = new TempDirectory();
        var gate = new TaskCompletionSource();
        var h = await CreateAsync(temp, _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingStream(Payload[..1000], gate.Task)) };
            r.Content.Headers.ContentLength = Payload.Length;
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            r.Headers.ETag = EntityTagHeaderValue.Parse("\"v1\"");
            return r;
        });
        var task = h.Manager.Enqueue(TestData.Item("Game", "0100000000010000", ContentType.Base, size: Payload.Length));
        await WaitForAsync(h.Manager, task.Id, s => s == DownloadStatus.Downloading);
        while (task.DownloadedBytes < 1000) await Task.Delay(10);

        h.Manager.Pause(task.Id);
        await WaitForAsync(h.Manager, task.Id, s => s == DownloadStatus.Paused);
        gate.SetResult();

        var record = Assert.Single(await h.Manifest.GetDownloadsAsync(default));
        Assert.Equal(DownloadStatus.Paused, record.Status);
        Assert.Equal("\"v1\"", record.ETag);
        Assert.Equal(1000, new FileInfo(record.PartialPath).Length);

        // A fresh manager (simulating an app restart) restores the paused transfer.
        var downloader = new HttpDownloader(TestData.Client(new FakeHandler(_ => FileResponse(Payload))), new BandwidthLimiter(), NullLogger<HttpDownloader>.Instance);
        var library = new LocalLibraryService(h.Manifest, h.Settings, NullLogger<LocalLibraryService>.Instance);
        var restarted = new DownloadManager(downloader, TestData.Links(h.Handler), h.Connections, h.Manifest, new CacheManager(library, h.Settings, NullLogger<CacheManager>.Instance),
            library, h.Settings, h.Paths, new BandwidthLimiter(), NullLogger<DownloadManager>.Instance);
        await restarted.InitializeAsync(default);

        var restored = Assert.Single(restarted.Tasks);
        Assert.Equal(DownloadStatus.Paused, restored.Status);
        Assert.Equal(1000, restored.DownloadedBytes);
    }

    [Fact]
    public async Task Download_from_another_profile_waits_until_that_profile_is_active()
    {
        using var temp = new TempDirectory();
        var other = TestData.Connection with { BaseUri = new Uri("https://second.example.com/") };
        var h = await CreateAsync(temp, _ => FileResponse(Payload), seed: (manifest, paths) => manifest.SaveDownloadAsync(new DownloadRecord
        {
            Id = "other1",
            ServerKey = other.ServerKey,
            Item = TestData.Item("Other Game", "0100000000030000", ContentType.Base, size: Payload.Length, fileId: 3),
            PartialPath = Path.Combine(paths.DownloadsDirectory, "other1.partial"),
            Status = DownloadStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        }, default));

        var task = Assert.Single(h.Manager.Tasks);
        Assert.Equal(DownloadStatus.Queued, task.Status);
        Assert.Contains("another server profile", task.Message);
        Assert.False(h.Manager.IsQueuedOrActive(task.Item.Key)); // not shown as queued while browsing the first server
        Assert.Empty(h.Handler.Requests);

        // Switching to the owning profile lets it run.
        h.Connections.Connection = other;
        await h.Settings.SaveAsync(h.Settings.Current);
        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());
        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal("second.example.com", h.Handler.Requests.Single().RequestUri!.Host);
    }

    [Fact]
    public async Task Orphaned_partial_files_are_cleaned_up_on_start()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("appdata"));
        paths.EnsureCreated();
        var orphan = Path.Combine(paths.DownloadsDirectory, "deadbeef.partial");
        await File.WriteAllTextAsync(orphan, "x");

        await CreateAsync(temp, _ => FileResponse(Payload));
        Assert.False(File.Exists(orphan));
    }

    /// <summary>Delivers a prefix immediately, then blocks until released (simulates a slow transfer).</summary>
    private sealed class BlockingStream(byte[] prefix, Task release) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            await release.WaitAsync(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public class LibraryProviderTests
{
    [Fact]
    public async Task Offline_startup_uses_cached_catalogue_marked_as_cached()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("appdata"));
        paths.EnsureCreated();
        var online = true;
        var handler = new FakeHandler(_ => online
            ? FakeHandler.Json("""{"sections":[{"id":"all","title":"All","total":1,"truncated":false,"items":[{"name":"A","app_id":"0100000000010000","app_type":"BASE","url":"/api/get_game/1#a.nsp","size":10,"file_id":1}]}]}""")
            : throw new HttpRequestException("Connection refused"));
        var provider = TestData.Provider(handler, new StaticConnectionProvider(TestData.Connection), paths);

        var live = await provider.GetCatalogAsync(default);
        Assert.False(live.FromCache);

        online = false;
        var cached = await provider.GetCatalogAsync(default);
        Assert.True(cached.FromCache);
        Assert.Single(cached.Items);
        Assert.Contains(cached.Warnings, w => w.Contains("Offline"));
    }

    [Fact]
    public async Task Offline_without_cache_reports_connectivity_failure()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("appdata"));
        paths.EnsureCreated();
        var handler = new FakeHandler(_ => throw new HttpRequestException("Connection refused"));
        var provider = TestData.Provider(handler, new StaticConnectionProvider(TestData.Connection), paths);

        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => provider.GetCatalogAsync(default));
        Assert.True(ex.IsConnectivityFailure);
    }

    [Fact]
    public async Task Cached_catalogue_for_another_server_is_ignored()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Combine("appdata"));
        paths.EnsureCreated();
        var connections = new StaticConnectionProvider(TestData.Connection);
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"sections":[{"id":"all","title":"All","items":[]}]}"""));
        var provider = TestData.Provider(handler, connections, paths);
        await provider.GetCatalogAsync(default);

        connections.Connection = TestData.Connection with { BaseUri = new Uri("https://other.example.com/") };
        Assert.Null(await provider.LoadCachedCatalogAsync(default));
    }
}

public class SecurityTests
{
    [Fact]
    public void Connection_to_string_never_contains_the_password() =>
        Assert.DoesNotContain("s3cret", TestData.Connection.ToString());

    [Fact]
    public async Task Settings_file_never_contains_the_password()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        await settings.SaveAsync(new AppSettings { ServerUrl = "https://aerofoil.example.com/", Username = "alice" });

        var json = await File.ReadAllTextAsync(paths.SettingsFile);
        Assert.DoesNotContain("password\":", json.Replace("rememberPassword\":", string.Empty), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("alice", json);
    }

    [Fact]
    public async Task Corrupt_settings_fall_back_to_defaults()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, "{ not json");

        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        Assert.False(settings.Current.SetupCompleted);
        Assert.True(File.Exists(paths.SettingsFile + ".corrupt"));
    }
}
