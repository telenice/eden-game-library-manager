using System.Security.Cryptography;
using System.Text;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using EdenGameLibraryManager.Infrastructure.Persistence;
using EdenGameLibraryManager.Infrastructure.Security;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

public class ProfileTests
{
    [Fact]
    public async Task Single_server_settings_from_1_0_are_migrated_to_a_default_profile()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, """
            {"serverUrl":"https://aerofoil.example.com/","username":"alice","rememberPassword":true,"allowInsecureHttp":false,
             "gamesDirectory":"D:\\Switch Games","cacheLimitGb":100,"setupCompleted":true}
            """);

        var settings = new SettingsService(paths, NullLogger<SettingsService>.Instance);
        var profile = Assert.Single(settings.Current.Profiles);
        Assert.Equal(SettingsService.MigratedProfileId, profile.Id);
        Assert.Equal("https://aerofoil.example.com/", profile.ServerUrl);
        Assert.Equal("alice", profile.Username);
        Assert.Equal(profile, settings.Current.ActiveProfile);
        Assert.Equal(100, settings.Current.CacheLimitGb);

        await settings.SaveAsync(settings.Current);
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(paths.SettingsFile));
        Assert.False(doc.RootElement.TryGetProperty("serverUrl", out _));
        Assert.False(doc.RootElement.TryGetProperty("username", out _));
        Assert.Equal(1, doc.RootElement.GetProperty("profiles").GetArrayLength());
        Assert.Single(new SettingsService(paths, NullLogger<SettingsService>.Instance).Current.Profiles);
    }

    [Fact]
    public void Active_profile_falls_back_to_first_and_duplicate_ids_are_repaired()
    {
        var normalized = SettingsService.Normalize(new AppSettings
        {
            Profiles =
            [
                new ServerProfile { Id = "a", ServerUrl = "https://one.example.com" },
                new ServerProfile { Id = "a", ServerUrl = "https://two.example.com" },
            ],
            ActiveProfileId = "missing",
        });

        Assert.Equal(2, normalized.Profiles.Select(p => p.Id).Distinct().Count());
        Assert.Equal("a", normalized.ActiveProfileId);
    }

    [Fact]
    public void Connection_uses_the_active_profile_and_its_own_password()
    {
        var credentials = new SessionCredentialStore();
        credentials.SetPassword("home", "home-pw", persist: false);
        credentials.SetPassword("work", "work-pw", persist: false);
        var settings = new InMemorySettings(new AppSettings
        {
            Profiles =
            [
                new ServerProfile { Id = "home", ServerUrl = "https://home.example.com", Username = "me" },
                new ServerProfile { Id = "work", ServerUrl = "https://work.example.com", Username = "me2" },
            ],
            ActiveProfileId = "work",
        });

        var connection = new SettingsConnectionProvider(settings, credentials).GetConnection()!;
        Assert.Equal("work.example.com", connection.BaseUri.Host);
        Assert.Equal("work-pw", connection.Password);
        Assert.Equal("home-pw", credentials.GetPassword("home"));
    }

    [Fact]
    public async Task Each_server_keeps_its_own_offline_catalogue()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        var connections = new StaticConnectionProvider(TestData.Connection);
        var online = true;
        var handler = new FakeHandler(r => online
            ? FakeHandler.Json($$"""{"sections":[{"id":"all","title":"All","items":[{"name":"{{r.RequestUri!.Host}}","app_id":"0100000000010000","app_type":"BASE","url":"/api/get_game/1#a.nsp","size":1,"file_id":1}]}]}""")
            : throw new HttpRequestException("offline"));
        var provider = new AeroFoilLibraryProvider(TestData.Client(handler), connections, paths, NullLogger<AeroFoilLibraryProvider>.Instance);

        await provider.GetCatalogAsync(default);
        var second = TestData.Connection with { BaseUri = new Uri("https://second.example.com/") };
        connections.Connection = second;
        await provider.GetCatalogAsync(default);

        online = false;
        connections.Connection = TestData.Connection;
        Assert.Equal("aerofoil.example.com", (await provider.GetCatalogAsync(default)).Items.Single().Name);
        connections.Connection = second;
        Assert.Equal("second.example.com", (await provider.GetCatalogAsync(default)).Items.Single().Name);
    }

    [WindowsFact]
    public void Password_saved_by_1_0_is_migrated_to_the_default_profile()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        var legacy = ProtectedData.Protect(Encoding.UTF8.GetBytes("old-pw"),
            Encoding.UTF8.GetBytes("EdenGameLibraryManager.credentials.v1"), DataProtectionScope.CurrentUser);
        File.WriteAllBytes(paths.LegacyCredentialsFile, legacy);

        var store = new DpapiCredentialStore(paths, NullLogger<DpapiCredentialStore>.Instance);
        Assert.Equal("old-pw", store.GetPassword(SettingsService.MigratedProfileId));
        Assert.False(File.Exists(paths.LegacyCredentialsFile));
        Assert.True(File.Exists(paths.CredentialsFileFor(SettingsService.MigratedProfileId)));
        Assert.Equal("old-pw", new DpapiCredentialStore(paths, NullLogger<DpapiCredentialStore>.Instance).GetPassword(SettingsService.MigratedProfileId));
        Assert.Null(store.GetPassword("someone-else"));
    }
}

public class GameGroupingTests
{
    [Fact]
    public void Updates_and_dlc_share_the_base_game_group()
    {
        var baseGame = TestData.Item("MK8", "0100152000022000", ContentType.Base);
        var update = TestData.Item("MK8 Update", "0100152000022800", ContentType.Update, version: "196608") with { TitleId = "0100152000022000" };
        var dlcWithoutTitleId = TestData.Item("Booster Pass", "0100152000023001", ContentType.Dlc) with { TitleId = null };
        var dlcWithOwnTitleId = TestData.Item("Booster Pass 2", "0100152000023002", ContentType.Dlc) with { TitleId = "0100152000023000" };
        var noAppId = TestData.Item("No app id", "0100152000022000", ContentType.Base) with { AppId = null };
        var other = TestData.Item("Other", "0100000000010000", ContentType.Base);

        Assert.Equal(GameGrouping.GroupKey(baseGame), GameGrouping.GroupKey(update));
        Assert.Equal(GameGrouping.GroupKey(baseGame), GameGrouping.GroupKey(dlcWithoutTitleId));
        Assert.Equal(GameGrouping.GroupKey(baseGame), GameGrouping.GroupKey(dlcWithOwnTitleId));
        Assert.Equal(GameGrouping.GroupKey(baseGame), GameGrouping.GroupKey(noAppId));
        Assert.NotEqual(GameGrouping.GroupKey(baseGame), GameGrouping.GroupKey(other));
    }

    [Fact]
    public void Members_are_ordered_base_then_newest_update_then_dlc()
    {
        var items = new[]
        {
            TestData.Item("DLC B", "0100152000023002", ContentType.Dlc),
            TestData.Item("Update old", "0100152000022800", ContentType.Update, version: "65536"),
            TestData.Item("Base", "0100152000022000", ContentType.Base),
            TestData.Item("DLC A", "0100152000023001", ContentType.Dlc),
            TestData.Item("Update new", "0100152000022800", ContentType.Update, version: "196608"),
        };
        Assert.Equal(["Base", "Update new", "Update old", "DLC A", "DLC B"], GameGrouping.OrderMembers(items).Select(i => i.Name));
    }

    [Theory]
    [InlineData("Mario Kart 8 Deluxe", "Mario Kart 8 Deluxe [0100152000022000]")]
    [InlineData("Zelda: Tears / Kingdom?", "Zelda_ Tears _ Kingdom_ [0100152000022000]")]
    [InlineData("..\\..\\Windows", "_.._Windows [0100152000022000]")]
    [InlineData("CON", "_CON [0100152000022000]")]
    [InlineData("   ", "Unknown game [0100152000022000]")]
    public void Game_folder_names_are_single_safe_segments(string name, string expected)
    {
        var folder = GameGrouping.FolderName(name, "0100152000022000");
        Assert.Equal(expected, folder);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, folder);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, folder);
    }

    [Fact]
    public void Update_folder_uses_the_base_title_name()
    {
        var update = TestData.Item("Mario Kart 8 Deluxe Update", "0100152000022800", ContentType.Update)
            with { TitleName = "Mario Kart 8 Deluxe", TitleId = "0100152000022000" };
        Assert.Equal("Mario Kart 8 Deluxe [0100152000022000]", GameGrouping.FolderName(update));
    }

    [Fact]
    public void Local_files_group_by_parsed_title_ids()
    {
        var update = TestData.Local("C:/g/upd.nsp", 1, managed: false, appId: "0100152000022800", type: ContentType.Update) with { TitleId = null };
        var baseGame = TestData.Local("C:/g/base.nsp", 1, managed: false, appId: "0100152000022000") with { TitleId = "0100152000022000" };
        Assert.Equal(GameGrouping.GroupKey(baseGame), GameGrouping.GroupKey(update));
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows-only (DPAPI).";
    }
}
