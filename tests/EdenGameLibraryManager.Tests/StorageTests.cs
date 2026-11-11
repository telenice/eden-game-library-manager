using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.Persistence;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

public class ManifestServiceTests
{
    [Fact]
    public async Task Round_trips_library_items_and_downloads()
    {
        using var temp = new TempDirectory();
        var manifest = new ManifestService(temp.Combine("library.db"), NullLogger<ManifestService>.Instance);
        await manifest.InitializeAsync(default);

        var stored = await manifest.UpsertLibraryItemAsync(TestData.Local(temp.Combine("g", "a.nsp"), 42, appId: "0100000000010000") with
        {
            Name = "A",
            DownloadedAt = DateTimeOffset.UtcNow,
        }, default);
        Assert.True(stored.Id > 0);

        await manifest.SetPinnedAsync(stored.Id, true, default);
        var item = Assert.Single(await manifest.GetLibraryItemsAsync(default));
        Assert.True(item.IsPinned);
        Assert.Equal("A", item.Name);

        var record = new DownloadRecord
        {
            Id = "d1",
            ServerKey = TestData.Connection.ServerKey,
            Item = TestData.Item("Game", "0100000000010000", ContentType.Base),
            PartialPath = temp.Combine("d1.partial"),
            TotalSize = 1000,
            DownloadedBytes = 400,
            ETag = "\"x\"",
            Status = DownloadStatus.Paused,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await manifest.SaveDownloadAsync(record, default);
        var loaded = Assert.Single(await manifest.GetDownloadsAsync(default));
        Assert.Equal("Game", loaded.Item.Name);
        Assert.Equal(DownloadStatus.Paused, loaded.Status);
        Assert.Equal("\"x\"", loaded.ETag);
    }

    [Fact]
    public async Task Recovers_from_a_corrupt_database_without_touching_games()
    {
        using var temp = new TempDirectory();
        var dbPath = temp.Combine("library.db");
        await File.WriteAllTextAsync(dbPath, "this is not a sqlite database at all, just garbage bytes.................");
        var game = temp.Combine("games", "keep.nsp");
        Directory.CreateDirectory(Path.GetDirectoryName(game)!);
        await File.WriteAllTextAsync(game, "game");

        var manifest = new ManifestService(dbPath, NullLogger<ManifestService>.Instance);
        await manifest.InitializeAsync(default);

        Assert.NotNull(manifest.RecoveredFromCorruptFile);
        Assert.True(File.Exists(manifest.RecoveredFromCorruptFile));
        Assert.Empty(await manifest.GetLibraryItemsAsync(default));
        Assert.True(File.Exists(game));
    }
}

public class LocalLibraryServiceTests
{
    private static async Task<(LocalLibraryService Library, ManifestService Manifest, string Games)> CreateAsync(TempDirectory temp)
    {
        var games = temp.Combine("Switch Games");
        Directory.CreateDirectory(games);
        var manifest = new ManifestService(temp.Combine("library.db"), NullLogger<ManifestService>.Instance);
        await manifest.InitializeAsync(default);
        var settings = new InMemorySettings(new AppSettings { GamesDirectory = games });
        return (new LocalLibraryService(manifest, settings, NullLogger<LocalLibraryService>.Instance), manifest, games);
    }

    [Fact]
    public async Task Detects_manual_files_in_nested_folders_and_ignores_other_files()
    {
        using var temp = new TempDirectory();
        var (library, _, games) = await CreateAsync(temp);
        Directory.CreateDirectory(Path.Combine(games, "Nintendo", "Racing"));
        await File.WriteAllBytesAsync(Path.Combine(games, "Nintendo", "Racing", "MK8 [0100152000022000][v0].xci"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(games, "notes.txt"), new byte[10]);
        await File.WriteAllBytesAsync(Path.Combine(games, "._MK8 [0100152000022000][v0].xci"), new byte[1]);
        await File.WriteAllBytesAsync(Path.Combine(games, "game.nsp.egl-copying"), new byte[1]);

        var result = await library.ScanAsync(default);

        var game = Assert.Single(result);
        Assert.False(game.IsManaged);
        Assert.Equal("0100152000022000", game.AppId);
        Assert.Equal(ContentType.Base, game.ContentType);
        Assert.Equal(0, game.Version);
    }

    [Fact]
    public async Task Removes_records_for_files_deleted_outside_the_app()
    {
        using var temp = new TempDirectory();
        var (library, manifest, games) = await CreateAsync(temp);
        var path = Path.Combine(games, "a.nsp");
        await File.WriteAllBytesAsync(path, new byte[5]);
        await library.RegisterDownloadAsync(TestData.Local(path, 5, appId: "0100000000010000"), default);

        File.Delete(path);
        var result = await library.ScanAsync(default);

        Assert.Empty(result);
        Assert.Empty(await manifest.GetLibraryItemsAsync(default));
    }

    [Fact]
    public async Task Managed_file_with_changed_size_becomes_unverified()
    {
        using var temp = new TempDirectory();
        var (library, _, games) = await CreateAsync(temp);
        var path = Path.Combine(games, "a.nsp");
        await File.WriteAllBytesAsync(path, new byte[5]);
        await library.RegisterDownloadAsync(TestData.Local(path, 5, appId: "0100000000010000"), default);
        await File.WriteAllBytesAsync(path, new byte[3]);

        var game = Assert.Single(await library.ScanAsync(default));
        Assert.Equal(LocalGameStatus.Unverified, game.Status);
        Assert.Equal(3, game.ActualSize);
    }

    [Fact]
    public async Task Missing_games_directory_does_not_wipe_the_manifest()
    {
        using var temp = new TempDirectory();
        var manifest = new ManifestService(temp.Combine("library.db"), NullLogger<ManifestService>.Instance);
        await manifest.InitializeAsync(default);
        await manifest.UpsertLibraryItemAsync(TestData.Local(temp.Combine("games", "a.nsp"), 5), default);

        // An invalid path (simulating an unplugged drive) that cannot be created.
        var settings = new InMemorySettings(new AppSettings { GamesDirectory = temp.Combine("file.txt", "games") });
        await File.WriteAllTextAsync(temp.Combine("file.txt"), "x");
        var library = new LocalLibraryService(manifest, settings, NullLogger<LocalLibraryService>.Instance);

        Assert.Empty(await library.ScanAsync(default));
        Assert.Single(await manifest.GetLibraryItemsAsync(default));
    }

    [Fact]
    public async Task Delete_refuses_files_outside_the_games_directory_or_non_games()
    {
        using var temp = new TempDirectory();
        var (library, _, _) = await CreateAsync(temp);
        var outside = temp.Combine("outside.nsp");
        await File.WriteAllTextAsync(outside, "x");

        await Assert.ThrowsAsync<UnsafePathException>(() => library.DeleteAsync(TestData.Local(outside, 1), default));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Move_puts_file_in_game_folder_and_updates_the_manifest()
    {
        using var temp = new TempDirectory();
        var (library, manifest, games) = await CreateAsync(temp);
        var path = Path.Combine(games, "a.nsp");
        await File.WriteAllBytesAsync(path, new byte[5]);
        var stored = await library.RegisterDownloadAsync(TestData.Local(path, 5, appId: "0100000000010000"), default);

        var target = Path.Combine(games, "Game [0100000000010000]");
        var moved = await library.MoveAsync(stored, target, default);

        Assert.Equal(Path.Combine(target, "a.nsp"), moved.LocalPath);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(moved.LocalPath));
        Assert.Equal(moved.LocalPath, Assert.Single(await manifest.GetLibraryItemsAsync(default)).LocalPath);
        Assert.True(Assert.Single(await library.ScanAsync(default)).IsManaged); // still recognised as downloaded by the app
    }

    [Fact]
    public async Task Move_refuses_destinations_outside_the_games_directory()
    {
        using var temp = new TempDirectory();
        var (library, _, games) = await CreateAsync(temp);
        var path = Path.Combine(games, "a.nsp");
        await File.WriteAllBytesAsync(path, new byte[5]);
        var stored = await library.RegisterDownloadAsync(TestData.Local(path, 5), default);

        await Assert.ThrowsAsync<UnsafePathException>(() => library.MoveAsync(stored, temp.Combine("elsewhere"), default));
        await Assert.ThrowsAsync<UnsafePathException>(() => library.MoveAsync(stored, Path.Combine(games, "..", "escape"), default));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Delete_removes_file_and_record()
    {
        using var temp = new TempDirectory();
        var (library, manifest, games) = await CreateAsync(temp);
        var path = Path.Combine(games, "a.nsp");
        await File.WriteAllBytesAsync(path, new byte[5]);
        var stored = await library.RegisterDownloadAsync(TestData.Local(path, 5), default);

        await library.DeleteAsync(stored, default);

        Assert.False(File.Exists(path));
        Assert.Empty(await manifest.GetLibraryItemsAsync(default));
        Assert.Empty(library.Games);
    }
}

public class CacheManagerTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public async Task Download_that_does_not_fit_throws_with_a_plan_and_deletes_nothing()
    {
        using var temp = new TempDirectory();
        var (library, settings) = await CreateLibraryAsync(temp, limitGb: 10.0 / 1024);
        await AddGameAsync(library, settings.Current.GamesDirectory, "old.nsp", 8 * MB, DateTimeOffset.UtcNow.AddDays(-3));

        var cache = new CacheManager(library, settings);
        var ex = await Assert.ThrowsAsync<CacheLimitExceededException>(() => cache.EnsureCapacityAsync(4 * MB, 0, default));
        Assert.Equal(2 * MB, ex.Plan.BytesToFree);
        Assert.Single(library.Games);
    }

    private static async Task<(LocalLibraryService, InMemorySettings)> CreateLibraryAsync(TempDirectory temp, double limitGb)
    {
        var games = temp.Combine("games");
        Directory.CreateDirectory(games);
        var manifest = new ManifestService(temp.Combine("library.db"), NullLogger<ManifestService>.Instance);
        await manifest.InitializeAsync(default);
        var settings = new InMemorySettings(new AppSettings { GamesDirectory = games, CacheLimitGb = limitGb });
        return (new LocalLibraryService(manifest, settings, NullLogger<LocalLibraryService>.Instance), settings);
    }

    private static async Task<LocalGame> AddGameAsync(LocalLibraryService library, string dir, string name, long size, DateTimeOffset when)
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, new byte[size]);
        return await library.RegisterDownloadAsync(TestData.Local(path, size, lastUsed: when), default);
    }
}

public class EdenPlayTimeTests
{
    private static byte[] Record(ulong programId, ulong seconds)
    {
        var bytes = new byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, programId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), seconds);
        return bytes;
    }

    [Fact]
    public void Parses_title_ids_and_seconds_and_ignores_a_partial_record()
    {
        byte[] data = [.. Record(0x0100152000022000, 1196), .. Record(0x01001B700BA7C000, 4352), .. Record(0, 5), 1, 2, 3];

        var times = EdenPlayTimeReader.Parse(data);

        Assert.Equal(2, times.Count);
        Assert.Equal(TimeSpan.FromSeconds(1196), times["0100152000022000"]);
        Assert.Equal(TimeSpan.FromSeconds(4352), times["01001b700ba7c000"]);
    }

    [Fact]
    public async Task Missing_file_means_play_time_is_unknown_and_changes_are_picked_up()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("play_time", "playtime.bin");
        var reader = new EdenPlayTimeReader(NullLogger<EdenPlayTimeReader>.Instance) { FilePathOverride = file };

        Assert.Null(await reader.GetPlayTimesAsync(default));

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, Record(0x0100152000022000, 60));
        Assert.Equal(TimeSpan.FromMinutes(1), (await reader.GetPlayTimesAsync(default))!["0100152000022000"]);

        await File.WriteAllBytesAsync(file, Record(0x0100152000022000, 120));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(2), (await reader.GetPlayTimesAsync(default))!["0100152000022000"]);
    }
}
