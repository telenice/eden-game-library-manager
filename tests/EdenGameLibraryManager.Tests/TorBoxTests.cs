using System.Net;
using System.Web;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.Storage;
using EdenGameLibraryManager.Infrastructure.TorBox;

namespace EdenGameLibraryManager.Tests;

public class TorBoxTests
{
    private const string ApiKey = "tb-key-123";
    private static readonly ServerConnection Connection = TorBoxClient.ConnectionFor(ApiKey);

    private const string TorrentList = """
        {"success":true,"data":[
          {"id":7,"name":"Pack","download_present":true,"files":[
            {"id":0,"name":"Pack/Zelda [01007EF00011E000][v0].nsp","short_name":"Zelda [01007EF00011E000][v0].nsp","size":1000},
            {"id":1,"name":"Pack/Zelda [01007EF00011E800][v786432].nsp","short_name":"Zelda [01007EF00011E800][v786432].nsp","size":200},
            {"id":2,"name":"Pack/readme.txt","short_name":"readme.txt","size":5}]},
          {"id":8,"name":"Still downloading","download_present":false,"files":[
            {"id":0,"short_name":"Other [0100000000010000][v0].nsp","size":10}]}]}
        """;

    private static HttpResponseMessage Api(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/v1/api/torrents/mylist" => FakeHandler.Json(TorrentList),
        "/v1/api/usenet/mylist" => FakeHandler.Json("""{"success":true,"data":[]}"""),
        "/v1/api/webdl/mylist" => FakeHandler.Json("""{"success":true,"data":[{"id":3,"download_present":true,"files":[{"id":4,"short_name":"Game.xci","size":9}]}]}"""),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Catalogue_lists_finished_game_files_from_every_download_list()
    {
        var handler = new FakeHandler(Api);
        var catalog = await TestData.TorBox(handler).GetCatalogAsync(Connection, default);

        Assert.Equal(3, catalog.Items.Count);
        var update = catalog.Items.Single(i => i.ContentType == ContentType.Update);
        Assert.Equal("01007EF00011E800", update.AppId);
        Assert.Equal("01007EF00011E000", update.TitleId);
        Assert.Equal(786432, update.NumericVersion);
        Assert.Equal("torbox:torrents/7/1", update.Key[4..]);
        Assert.Equal("Zelda [01007EF00011E800][v786432].nsp", update.SourceFileName);
        Assert.Contains(catalog.Items, i => i.Url.StartsWith("torbox:webdl/3/4#", StringComparison.Ordinal));

        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer", r.Headers.Authorization?.Scheme);
            Assert.Equal(ApiKey, r.Headers.Authorization?.Parameter);
        });
    }

    [Fact]
    public void Base_update_and_dlc_in_one_torrent_are_grouped_under_the_base_game()
    {
        const string json = """
            {"id":5,"download_present":true,"files":[
              {"id":0,"name":"Mario Kart™ 8 Deluxe [0100152000022000]/Base/Mario Kart™ 8 Deluxe [0100152000022000] [BASE][v0].nsp","size":7268474425},
              {"id":1,"name":"Mario Kart™ 8 Deluxe [0100152000022000]/DLC/Wave 1 [0100152000023001]/Mario Kart™ 8 Deluxe - Wave 1 [0100152000023001] [DLC][v65536].nsp","size":124112},
              {"id":2,"name":"Mario Kart™ 8 Deluxe [0100152000022000]/Updates/v1507328/Mario Kart™ 8 Deluxe [0100152000022800] [UPDATE][v1507328].nsp","size":4861420107}]}
            """;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = TorBoxClient.ReadDownload("torrents", doc.RootElement).ToList();

        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal("0100152000022000", EdenGameLibraryManager.Core.Services.GameGrouping.GroupKey(i)));
        Assert.Equal((ContentType.Base, 0L), (items[0].ContentType, items[0].NumericVersion));
        Assert.Equal((ContentType.Dlc, 65536L), (items[1].ContentType, items[1].NumericVersion));
        Assert.Equal((ContentType.Update, 1507328L), (items[2].ContentType, items[2].NumericVersion));
        Assert.Equal("Mario Kart™ 8 Deluxe [0100152000022800] [UPDATE][v1507328].nsp", items[2].SourceFileName);
    }

    [Fact]
    public async Task Rejected_api_key_is_reported_as_invalid_credentials()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"detail":"Not authenticated"}""", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.TorBox(handler).GetCatalogAsync(Connection, default));
        Assert.Equal(AeroFoilErrorKind.InvalidCredentials, ex.Kind);
        Assert.DoesNotContain(ApiKey, ex.Message);
    }

    [Fact]
    public async Task Download_link_request_names_the_file_id_parameter()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"success":true,"data":"https://nexus-1.neur.tb-cdn.st/dld/abc?token=tb-key-123"}"""));
        var item = new RemoteGameItem { Name = "Zelda", Url = "torbox:usenet/42/3#Zelda.nsp" };

        var link = await TestData.TorBox(handler).RequestDownloadLinkAsync(Connection, item, default);

        Assert.Equal("nexus-1.neur.tb-cdn.st", link.Host);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v1/api/usenet/requestdl", request.RequestUri!.AbsolutePath);
        var query = HttpUtility.ParseQueryString(request.RequestUri.Query);
        Assert.Equal(ApiKey, query["token"]);
        Assert.Equal("42", query["usenet_id"]);
        Assert.Equal("3", query["file_id"]);
        Assert.Equal("false", query["zip_link"]);
    }

    [Fact]
    public async Task Plain_http_download_links_are_refused()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"success":true,"data":"http://nexus-1.neur.tb-cdn.st/file"}"""));
        var item = new RemoteGameItem { Name = "Zelda", Url = "torbox:torrents/1/0#Zelda.nsp" };
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.TorBox(handler).RequestDownloadLinkAsync(Connection, item, default));
        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, ex.Kind);
    }

    [Fact]
    public async Task Cdn_redirect_to_another_host_is_not_followed()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        var handler = new FakeHandler(r =>
        {
            if (r.RequestUri!.Host == "api.torbox.app")
                return FakeHandler.Json("""{"success":true,"data":"https://nexus-1.neur.tb-cdn.st/dld/abc?token=tb-key-123"}""");
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://elsewhere.example.com/file?token=tb-key-123");
            return redirect;
        });
        var item = new RemoteGameItem { Name = "Zelda", Url = "torbox:torrents/1/0#Zelda.nsp" };
        var link = await TestData.Links(handler).ResolveAsync(Connection, item, default);

        using var request = new HttpRequestMessage(HttpMethod.Get, link.Uri);
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).SendAsync(link.Connection, request, default));

        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, ex.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Host == "elsewhere.example.com");
    }

    [Theory]
    [InlineData("/api/get_game/1#a.nsp")]
    [InlineData("torbox:torrents/abc/0")]
    [InlineData("torbox:other/1/0")]
    [InlineData("https://evil.example.com/torbox:torrents/1/0")]
    public void Non_torbox_items_are_rejected(string url) =>
        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, Assert.Throws<AeroFoilException>(() => TorBoxClient.ParseItemUrl(url)).Kind);

    [Fact]
    public void Each_api_key_gets_its_own_server_key_and_the_key_is_never_shown()
    {
        var other = TorBoxClient.ConnectionFor("another-key");
        Assert.NotEqual(Connection.ServerKey, other.ServerKey);
        Assert.StartsWith("torbox:", Connection.ServerKey);
        Assert.DoesNotContain(ApiKey, Connection.ServerKey);
        Assert.DoesNotContain(ApiKey, Connection.ToString());
        Assert.False(Connection.HasCredentials);
    }
}

public partial class DownloadManagerTests
{
    [Fact]
    public async Task TorBox_file_is_fetched_from_a_fresh_cdn_link_without_extra_credentials()
    {
        using var temp = new TempDirectory();
        var linkRequests = 0;
        var h = await CreateAsync(temp, r =>
        {
            if (r.RequestUri!.Host == "api.torbox.app")
            {
                linkRequests++;
                return FakeHandler.Json("""{"success":true,"data":"https://nexus-1.neur.tb-cdn.st/dld/abc?token=tb-key-123"}""");
            }
            return FileResponse(Payload);
        });
        h.Connections.Connection = TorBoxClient.ConnectionFor("tb-key-123");
        var item = new RemoteGameItem
        {
            Name = "Zelda",
            AppId = "01007EF00011E000",
            TitleId = "01007EF00011E000",
            ContentType = ContentType.Base,
            AppVersion = "0",
            Size = Payload.Length,
            Filename = "Zelda [01007EF00011E000][v0].nsp",
            Url = "torbox:torrents/7/0#Zelda.nsp",
        };

        var task = h.Manager.Enqueue(item);
        await WaitForAsync(h.Manager, task.Id, s => s.IsTerminal());

        Assert.Equal(DownloadStatus.Completed, task.Status);
        Assert.Equal(1, linkRequests);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(task.CompletedPath!));
        // The link is used exactly as TorBox issued it; nothing else (no Bearer header) is added.
        var cdn = Assert.Single(h.Handler.Requests, r => r.RequestUri!.Host == "nexus-1.neur.tb-cdn.st");
        Assert.Null(cdn.Headers.Authorization);
    }

    [Fact]
    public async Task TorBox_profile_refuses_items_from_an_aerofoil_catalogue()
    {
        using var temp = new TempDirectory();
        var h = await CreateAsync(temp, _ => FileResponse(Payload));
        h.Connections.Connection = TorBoxClient.ConnectionFor("tb-key-123");
        var ex = Assert.Throws<AeroFoilException>(() => h.Manager.Enqueue(TestData.Item("Game", "0100000000010000", ContentType.Base)));
        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, ex.Kind);
    }
}
