using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.Tests;

public class LibraryMatcherTests
{
    private const string Mk8 = "0100152000022000";
    private const string Mk8Update = "0100152000022800";
    private const string Mk8Dlc = "0100152000023001";

    [Fact]
    public void Acceptance_example_hides_only_games_present_locally()
    {
        var remote = new[]
        {
            TestData.Item("Mario Kart 8 Deluxe", Mk8, ContentType.Base, fileId: 1),
            TestData.Item("Mario Party Superstars", "01006FE013472000", ContentType.Base, fileId: 2),
            TestData.Item("Super Mario Odyssey", "0100000000010000", ContentType.Base, fileId: 3),
            TestData.Item("Luigi's Mansion 3", "0100DCA0064A6000", ContentType.Base, fileId: 4),
        };
        var local = new[]
        {
            TestData.Local("C:/g/mk8.nsp", 1000, managed: true, appId: Mk8),
            TestData.Local("C:/g/Super Mario Odyssey [0100000000010000][v0].nsp", 999, managed: false, appId: "0100000000010000"),
        };

        var matches = LibraryMatcher.MatchAll(remote, local);
        var available = remote.Where(r => matches[r.Key].State != MatchState.Downloaded).Select(r => r.Name).ToList();

        Assert.Equal(["Mario Party Superstars", "Luigi's Mansion 3"], available);
    }

    [Fact]
    public void Base_game_is_not_downloaded_because_update_or_dlc_exists()
    {
        var baseGame = TestData.Item("MK8", Mk8, ContentType.Base);
        var local = new[]
        {
            TestData.Local("C:/g/update.nsp", 10, managed: true, appId: Mk8Update, type: ContentType.Update, version: 196608),
            TestData.Local("C:/g/dlc.nsp", 10, managed: true, appId: Mk8Dlc, type: ContentType.Dlc),
        };
        Assert.Equal(MatchState.NotDownloaded, LibraryMatcher.Match(baseGame, local).State);
    }

    [Fact]
    public void Different_versions_are_distinct()
    {
        var newUpdate = TestData.Item("MK8 Update", Mk8Update, ContentType.Update, version: "196608");
        var oldLocal = TestData.Local("C:/g/upd.nsp", 1000, managed: true, appId: Mk8Update, type: ContentType.Update, version: 131072);
        Assert.Equal(MatchState.NotDownloaded, LibraryMatcher.Match(newUpdate, [oldLocal]).State);

        var sameLocal = oldLocal with { Version = 196608 };
        Assert.Equal(MatchState.Downloaded, LibraryMatcher.Match(newUpdate, [sameLocal]).State);
    }

    [Fact]
    public void Manual_file_without_version_needs_matching_size()
    {
        var item = TestData.Item("Game", "0100000000010000", ContentType.Base, size: 5000);
        var noVersionSameSize = TestData.Local("C:/g/Game [0100000000010000].nsp", 5000, managed: false, appId: "0100000000010000", version: null);
        var noVersionOtherSize = noVersionSameSize with { ActualSize = 4000 };

        Assert.Equal(MatchState.Downloaded, LibraryMatcher.Match(item, [noVersionSameSize]).State);
        Assert.Equal(MatchState.PossibleMatch, LibraryMatcher.Match(item, [noVersionOtherSize]).State);
    }

    [Fact]
    public void Unverified_managed_file_is_only_a_possible_match()
    {
        var item = TestData.Item("Game", "0100000000010000", ContentType.Base, size: 5000);
        var damaged = TestData.Local("C:/g/game.nsp", 4000, managed: true, appId: "0100000000010000") with { Status = LocalGameStatus.Unverified };
        Assert.Equal(MatchState.PossibleMatch, LibraryMatcher.Match(item, [damaged]).State);
    }

    [Fact]
    public void Exact_filename_and_size_match_without_ids()
    {
        var item = TestData.Item("Game", "0100000000010000", ContentType.Base, size: 5000, filename: "Game.nsp");
        var local = TestData.Local("C:/g/sub/Game.nsp", 5000, managed: false, appId: null, version: null);
        Assert.Equal(MatchState.Downloaded, LibraryMatcher.Match(item, [local]).State);
    }

    [Fact]
    public void Matching_by_filename_alone_is_not_enough_when_size_differs()
    {
        var item = TestData.Item("Game", "0100000000010000", ContentType.Base, size: 5000, filename: "Game.nsp");
        var local = TestData.Local("C:/g/Game.nsp", 10, managed: false, appId: null, version: null);
        Assert.Equal(MatchState.NotDownloaded, LibraryMatcher.Match(item, [local]).State);
    }
}

public class TitleIdHelperTests
{
    [Theory]
    [InlineData("0100152000022000", ContentType.Base)]
    [InlineData("0100152000022800", ContentType.Update)]
    [InlineData("0100152000023001", ContentType.Dlc)]
    [InlineData("nothex", ContentType.Unknown)]
    public void Classifies_application_ids(string id, ContentType expected) =>
        Assert.Equal(expected, TitleIdHelper.ClassifyAppId(id));

    [Theory]
    [InlineData("0100152000022800", "0100152000022000")]
    [InlineData("0100152000023001", "0100152000022000")]
    public void Finds_base_title_id(string appId, string expected) =>
        Assert.Equal(expected, TitleIdHelper.BaseTitleIdOf(appId));

    [Fact]
    public void Parses_bracketed_tokens_from_file_names()
    {
        var id = TitleIdHelper.ParseFileName("Mario Kart 8 Deluxe [0100152000022800][v196608] (1.5 GB).nsp");
        Assert.Equal("0100152000022800", id.AppId);
        Assert.Equal(196608, id.Version);
        Assert.Equal(ContentType.Update, id.ContentType);

        var none = TitleIdHelper.ParseFileName("my game.xci");
        Assert.Null(none.AppId);
        Assert.Null(none.Version);
    }

    [Theory]
    [InlineData("BASE", ContentType.Base)]
    [InlineData("UPDATE", ContentType.Update)]
    [InlineData("DLC", ContentType.Dlc)]
    [InlineData("130", ContentType.Dlc)]
    public void Parses_aerofoil_app_types(string raw, ContentType expected) =>
        Assert.Equal(expected, TitleIdHelper.ParseContentType(raw, null));
}

public class PathSafetyTests
{
    [Theory]
    [InlineData("../../evil.nsp")]
    [InlineData("..\\..\\Windows\\evil.nsp")]
    [InlineData("C:\\temp\\x.nsp")]
    [InlineData("/etc/passwd.nsp")]
    [InlineData("..")]
    [InlineData("")]
    public void Rejects_traversal_and_rooted_names(string name) =>
        Assert.Throws<UnsafePathException>(() => PathSafety.SanitizeGameFileName(name));

    [Theory]
    [InlineData("game.exe")]
    [InlineData("game.nsp.bat")]
    [InlineData("readme.txt")]
    public void Rejects_non_game_extensions(string name) =>
        Assert.Throws<UnsafePathException>(() => PathSafety.SanitizeGameFileName(name));

    [Fact]
    public void Cleans_dangerous_characters_and_reserved_names()
    {
        Assert.Equal("Game_ Part_2_.nsp", PathSafety.SanitizeGameFileName("Game? Part*2|.nsp"));
        Assert.Equal("_CON.nsp", PathSafety.SanitizeGameFileName("CON.nsp"));
        Assert.Equal("Game.xcz", PathSafety.SanitizeGameFileName("Game.XCZ"));
    }

    [Fact]
    public void Containment_checks_resolve_relative_segments()
    {
        using var temp = new TempDirectory();
        Assert.True(PathSafety.IsWithinDirectory(temp.Path, temp.Combine("a", "b.nsp")));
        Assert.False(PathSafety.IsWithinDirectory(temp.Path, temp.Combine("..", "b.nsp")));
        Assert.False(PathSafety.IsWithinDirectory(temp.Path, temp.Path + "-sibling" + Path.DirectorySeparatorChar + "b.nsp"));
    }

    [Fact]
    public void Never_overwrites_existing_files()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("game.nsp"), "x");
        Assert.Equal(temp.Combine("game (1).nsp"), PathSafety.GetAvailablePath(temp.Path, "game.nsp"));
    }
}

public class UrlPolicyTests
{
    private static readonly Uri Base = new("https://aerofoil.example.com/");

    [Fact]
    public void Requires_https_unless_explicitly_allowed()
    {
        var ex = Assert.Throws<AeroFoilException>(() => UrlPolicy.NormalizeServerUrl("http://192.168.1.10:8465", false));
        Assert.Equal(AeroFoilErrorKind.InsecureConnection, ex.Kind);
        Assert.Equal("http://192.168.1.10:8465/", UrlPolicy.NormalizeServerUrl("http://192.168.1.10:8465", true).ToString());
        Assert.Equal("https://aerofoil.example.com/", UrlPolicy.NormalizeServerUrl("aerofoil.example.com", false).ToString());
    }

    [Fact]
    public void Rejects_credentials_in_url() =>
        Assert.Throws<AeroFoilException>(() => UrlPolicy.NormalizeServerUrl("https://user:pw@aerofoil.example.com", false));

    [Theory]
    [InlineData("https://evil.example.net/api/get_game/1")]
    [InlineData("//evil.example.net/api/get_game/1")]
    [InlineData("http://aerofoil.example.com/api/get_game/1")]
    [InlineData("https://aerofoil.example.com:8443/api/get_game/1")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://user@aerofoil.example.com/x")]
    public void Rejects_download_urls_outside_the_trusted_origin(string url)
    {
        var ex = Assert.Throws<AeroFoilException>(() => UrlPolicy.ResolveTrusted(Base, url));
        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, ex.Kind);
    }

    [Fact]
    public void Resolves_relative_urls_and_strips_fragment()
    {
        var uri = UrlPolicy.ResolveTrusted(Base, "/api/get_game/12#Mario Party.nsp");
        Assert.Equal("https://aerofoil.example.com/api/get_game/12", uri.ToString());
    }
}

public class CachePlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void Specification_example_detects_limit_exceeded()
    {
        var games = new[]
        {
            TestData.Local("a", 12 * GB, lastUsed: Now.AddDays(-10), id: 1),
            TestData.Local("b", 4 * GB, lastUsed: Now.AddDays(-30), id: 2),
            TestData.Local("c", 6 * GB, lastUsed: Now.AddDays(-5), id: 3),
            TestData.Local("d", 18 * GB, lastUsed: Now.AddDays(-1), id: 4),
            TestData.Local("e", 40 * GB, lastUsed: Now, id: 5),
        };
        var plan = CachePlanner.Plan(100 * GB, 80 * GB, 0, 25 * GB, games);

        Assert.False(plan.Fits);
        Assert.Equal(5 * GB, plan.BytesToFree);
        // Least recently used first: b (4 GB) then a (12 GB).
        Assert.Equal(["b", "a"], plan.Evictions.Select(e => e.LocalPath));
        Assert.True(plan.CanSatisfyByEviction);
    }

    [Fact]
    public void Pinned_and_manual_files_are_never_evicted()
    {
        var games = new[]
        {
            TestData.Local("pinned", 50 * GB, pinned: true, lastUsed: Now.AddDays(-100), id: 1),
            TestData.Local("manual", 50 * GB, managed: false, lastUsed: Now.AddDays(-100), id: 2),
            TestData.Local("recent", 10 * GB, lastUsed: Now, id: 3),
        };
        var plan = CachePlanner.Plan(60 * GB, 60 * GB, 0, 5 * GB, games);

        Assert.Equal(["recent"], plan.Evictions.Select(e => e.LocalPath));
    }

    [Fact]
    public void Protected_files_are_skipped_and_shortfall_reported()
    {
        var games = new[] { TestData.Local("inuse", 10 * GB, id: 1) };
        var plan = CachePlanner.Plan(10 * GB, 10 * GB, 0, 5 * GB, games, _ => true);

        Assert.Empty(plan.Evictions);
        Assert.False(plan.CanSatisfyByEviction);
        Assert.Equal(5 * GB, plan.Shortfall);
    }

    [Fact]
    public void Reserved_transfer_space_counts_toward_the_limit()
    {
        var plan = CachePlanner.Plan(100 * GB, 50 * GB, 45 * GB, 10 * GB, []);
        Assert.Equal(5 * GB, plan.BytesToFree);
    }

    [Fact]
    public void Unlimited_cache_always_fits() =>
        Assert.True(CachePlanner.Plan(0, 999 * GB, 999 * GB, 999 * GB, []).Fits);
}
