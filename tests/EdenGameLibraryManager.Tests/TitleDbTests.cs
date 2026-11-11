using System.IO.Compression;
using System.Net;
using System.Text;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.Library;
using EdenGameLibraryManager.Infrastructure.Storage;
using EdenGameLibraryManager.Infrastructure.TitleDb;
using EdenGameLibraryManager.Infrastructure.TorBox;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

public class TitleDbTests
{
    private const string Icon = "https://img-eshop.cdn.nintendo.net/i/icon.jpg";

    private const string RegionJson = $$"""
        {
          "70010000000025": {"id":"01007EF00011E000","name":"Zelda","description":"Adventure","iconUrl":"{{Icon}}",
                             "screenshots":["https://img-eshop.cdn.nintendo.net/i/s1.jpg","https://evil.example.com/s2.jpg"],
                             "releaseDate":20170303,"category":["Adventure","RPG"],"publisher":"Nintendo"},
          "70010000000026": {"id":"01007EF00011F001","name":"Zelda Expansion Pass","iconUrl":"https://evil.example.com/x.jpg"},
          "70010000000027": {"id":null,"name":"No id"},
          "70010000000028": {"id":"not-an-id","name":"Bad id"},
          "70010000000029": null,
          "70010000000030": [1, {"nested": true}],
          "70010000000031": {"id":"01007EF00011E000","name":"Zelda duplicate without art"}
        }
        """;

    /// <summary>Returns at most a few bytes per read, so every entry spans several buffer refills.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task Parser_streams_entries_and_skips_invalid_ones(int chunk)
    {
        var entries = new List<TitleInfo>();
        await TitleDatabase.ParseAsync(new TrickleStream(Encoding.UTF8.GetBytes(RegionJson), chunk), entries.Add, default);

        Assert.Equal(["01007EF00011E000", "01007EF00011F001", "01007EF00011E000"], entries.Select(e => e.Id));
        var zelda = entries[0];
        Assert.Equal("Zelda", zelda.Name);
        Assert.Equal(Icon, zelda.IconUrl);
        Assert.Equal(["https://img-eshop.cdn.nintendo.net/i/s1.jpg"], zelda.ScreenshotUrls);
        Assert.Equal(new DateOnly(2017, 3, 3), zelda.ReleaseDate);
        Assert.Equal("Adventure, RPG", zelda.Category);
        // Artwork from any other host is dropped.
        Assert.Null(entries[1].IconUrl);
    }

    [Fact]
    public async Task Truncated_file_is_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes(RegionJson[..200]);
        await Assert.ThrowsAnyAsync<Exception>(() => TitleDatabase.ParseAsync(new MemoryStream(bytes), _ => { }, default));
    }

    [Fact]
    public async Task Refresh_downloads_gzip_json_then_revalidates_with_etag()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        var settings = new InMemorySettings(new AppSettings { TitleDbRegion = "GB.en" });
        var handler = new FakeHandler(r =>
        {
            if (r.Headers.TryGetValues("If-None-Match", out _)) return new HttpResponseMessage(HttpStatusCode.NotModified);
            using var gz = new MemoryStream();
            using (var zip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
                zip.Write(Encoding.UTF8.GetBytes(RegionJson));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(gz.ToArray()) };
            response.Content.Headers.ContentEncoding.Add("gzip");
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"", isWeak: true);
            return response;
        });
        var db = new TitleDatabase(new HttpClient(handler), settings, paths, NullLogger<TitleDatabase>.Instance);
        var updates = 0;
        db.Updated += (_, _) => updates++;

        await db.RefreshAsync(force: false, default);

        Assert.Equal("https://raw.githubusercontent.com/blawar/titledb/master/GB.en.json", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(1, updates);
        var status = db.GetStatus();
        Assert.True(status.IsAvailable);
        Assert.Equal("GB.en", status.Region);
        Assert.Equal(2, status.TitleCount);
        var found = await db.LookupAsync(["01007ef00011e000", "01007EF00011F001", "0100000000010000"], default);
        Assert.Equal(2, found.Count);
        // The duplicate without artwork didn't replace the full entry.
        Assert.Equal("Zelda", found["01007EF00011E000"].Name);
        Assert.Equal("Adventure", found["01007EF00011E000"].Description);

        // Fresh copy: no request at all. Forced: revalidated with the stored ETag and kept.
        await db.RefreshAsync(force: false, default);
        Assert.Single(handler.Requests);
        await db.RefreshAsync(force: true, default);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("W/\"abc\"", handler.Requests[1].Headers.GetValues("If-None-Match").Single());
        Assert.Equal(1, updates);
        Assert.Null(db.GetStatus().LastError);
        Assert.Equal(2, (await db.LookupAsync(["01007EF00011E000", "01007EF00011F001"], default)).Count);

        // Turned off: lookups return nothing.
        await settings.SaveAsync(settings.Current with { UseTitleDb = false });
        Assert.Empty(await db.LookupAsync(["01007EF00011E000"], default));
    }

    [Fact]
    public async Task Failed_refresh_keeps_the_previous_copy_and_reports_the_error()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var db = new TitleDatabase(new HttpClient(handler), new InMemorySettings(new AppSettings()), paths, NullLogger<TitleDatabase>.Instance);

        await db.RefreshAsync(force: true, default);

        var status = db.GetStatus();
        Assert.False(status.IsAvailable);
        Assert.Contains("500", status.LastError);
        Assert.False(File.Exists(paths.TitleDbFile + ".tmp"));
    }

    private static readonly Dictionary<string, TitleInfo> Titles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["01007EF00011E000"] = new() { Id = "01007EF00011E000", Name = "Zelda", IconUrl = Icon, ReleaseDate = new DateOnly(2017, 3, 3), Category = "Adventure" },
        ["01007EF00011F001"] = new() { Id = "01007EF00011F001", Name = "Zelda Expansion Pass" },
    };

    [Theory]
    [InlineData("Zelda [01007EF00011E000][v0].nsp", "Zelda")]
    [InlineData("z [01007EF00011E800][v786432].nsp", "Zelda (Update)")]
    [InlineData("z [01007EF00011F001][v0].nsp", "Zelda Expansion Pass")]
    [InlineData("z [01007EF00011F002][v0].nsp", "Zelda (DLC)")]
    public void TorBox_items_take_their_names_and_artwork_from_titledb(string fileName, string expected)
    {
        var json = $$"""{"id":1,"download_present":true,"files":[{"id":0,"short_name":"{{fileName}}","size":1}]}""";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var item = TorBoxClient.ReadDownload("torrents", doc.RootElement).Single();

        var enriched = ServerLibraryProvider.Enrich(item, Titles, preferTitleDb: true);

        Assert.Equal(expected, enriched.Name);
        Assert.Equal("Zelda", enriched.TitleName);
        Assert.Equal(Icon, enriched.IconUrl);
        Assert.Equal("Zelda [01007EF00011E000]", GameGrouping.FolderName(enriched));
    }

    [Fact]
    public void AeroFoil_items_keep_server_values_and_only_gain_missing_ones()
    {
        var item = TestData.Item("Server name", "01007EF00011E000", ContentType.Base) with { IconUrl = "/api/shop/icon/01007EF00011E000" };
        var enriched = ServerLibraryProvider.Enrich(item, Titles, preferTitleDb: false);

        Assert.Equal("Server name", enriched.Name);
        Assert.Equal("/api/shop/icon/01007EF00011E000", enriched.IconUrl);
        Assert.Equal(new DateOnly(2017, 3, 3), enriched.ReleaseDate);
        Assert.Equal("Adventure", enriched.Category);
    }

    [Fact]
    public async Task Provider_enriches_torbox_catalogues()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        var handler = new FakeHandler(r => FakeHandler.Json(r.RequestUri!.AbsolutePath == "/v1/api/torrents/mylist"
            ? """{"success":true,"data":[{"id":1,"download_present":true,"files":[{"id":0,"short_name":"x [01007EF00011E000][v0].nsp","size":1}]}]}"""
            : """{"success":true,"data":[]}"""));
        var provider = TestData.Provider(handler, new StaticConnectionProvider(TorBoxClient.ConnectionFor("k")), paths,
            new FakeTitleDatabase([.. Titles.Values]));

        var catalog = await provider.GetCatalogAsync(default);

        Assert.Equal("Zelda", catalog.Items.Single().Name);
        Assert.Equal("Zelda", (await provider.LoadCachedCatalogAsync(default))!.Items.Single().Name);
    }

    [Theory]
    [InlineData("https://img-eshop.cdn.nintendo.net/i/a.jpg", true)]
    [InlineData("http://img-eshop.cdn.nintendo.net/i/a.jpg", false)]
    [InlineData("https://img-eshop.cdn.nintendo.net:8443/i/a.jpg", false)]
    [InlineData("https://user@img-eshop.cdn.nintendo.net/i/a.jpg", false)]
    [InlineData("https://img-eshop.cdn.nintendo.net.evil.com/i/a.jpg", false)]
    [InlineData("/api/shop/icon/0100000000010000", false)]
    public void Only_titledb_image_host_is_public(string url, bool expected) =>
        Assert.Equal(expected, UrlPolicy.IsPublicArtworkUrl(url));
}
