using System.Net;
using System.Net.Http.Headers;
using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.Tests;

public class AeroFoilClientTests
{
    // Shape produced by AeroFoil's _build_shop_sections_payload (app/app.py).
    private const string SectionsJson = """
        {
          "rotation_key": "2026-11-08",
          "sections": [
            {"id": "new", "title": "New", "items": [
              {"name": "Mario Party Superstars", "title_name": "Mario Party Superstars", "title_id": "01006FE013472000",
               "app_id": "01006FE013472000", "app_version": "0", "app_type": "BASE", "category": "Party",
               "icon_url": "/api/shop/icon/01006FE013472000", "url": "/api/get_game/12#Mario Party Superstars [01006FE013472000][v0].nsp",
               "size": 2900000000, "file_id": 12, "filename": "Mario Party Superstars [01006FE013472000][v0].nsp", "download_count": 3}
            ]},
            {"id": "recommended", "title": "Recommended", "items": []},
            {"id": "updates", "title": "Updates", "items": [
              {"name": "Mario Kart 8 Deluxe", "title_id": "0100152000022000", "app_id": "0100152000022800", "app_version": "196608",
               "app_type": "UPDATE", "url": "/api/get_game/7#MK8 Update [0100152000022800][v196608].nsp", "size": 1500000, "file_id": 7}
            ]},
            {"id": "dlc", "title": "DLC", "items": [
              {"name": "Booster Course Pass", "title_id": "0100152000022000", "app_id": "0100152000023001", "app_version": "0",
               "app_type": "DLC", "url": "/api/get_game/8#BCP [0100152000023001][v0].nsp", "size": 2000000, "file_id": 8}
            ]},
            {"id": "all", "title": "All", "total": 4, "truncated": false, "items": [
              {"name": "Mario Kart 8 Deluxe", "title_id": "0100152000022000", "app_id": "0100152000022000", "app_version": 0,
               "app_type": "BASE", "url": "/api/get_game/5#MK8 [0100152000022000][v0].xci", "size": "7000000000", "file_id": 5},
              {"name": "Mario Party Superstars", "title_id": "01006FE013472000", "app_id": "01006FE013472000", "app_version": "0",
               "app_type": "BASE", "url": "/api/get_game/12#Mario Party Superstars [01006FE013472000][v0].nsp", "size": 2900000000, "file_id": 12},
              {"name": "Mario Kart 8 Deluxe", "title_id": "0100152000022000", "app_id": "0100152000022800", "app_version": "196608",
               "app_type": "UPDATE", "url": "/api/get_game/7#MK8 Update [0100152000022800][v196608].nsp", "size": 1500000, "file_id": 7},
              {"name": "Booster Course Pass", "title_id": "0100152000022000", "app_id": "0100152000023001", "app_version": "0",
               "app_type": "DLC", "url": "/api/get_game/8#BCP [0100152000023001][v0].nsp", "size": 2000000, "file_id": 8}
            ]},
            {"id": "cheats", "title": "Cheats", "items": [{"name": "cheat", "url": "/api/cheats/0100152000022000/ABCDEF0123456789"}]}
          ]
        }
        """;

    [Fact]
    public async Task Parses_catalogue_and_deduplicates_overlapping_sections()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/api/remote/sections"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : FakeHandler.Json(SectionsJson));

        var result = await TestData.Client(handler).GetCatalogAsync(TestData.Connection, default);

        Assert.Equal(4, result.Items.Count);
        Assert.True(result.IsComplete);
        Assert.Equal("/api/shop/sections", result.Endpoint);
        Assert.Equal(2, result.Items.Count(i => i.ContentType == ContentType.Base));
        Assert.Single(result.Items, i => i.ContentType == ContentType.Update);
        Assert.Single(result.Items, i => i.ContentType == ContentType.Dlc);
        Assert.DoesNotContain(result.Items, i => i.Url.StartsWith("/api/cheats"));

        var mk8 = result.Items.Single(i => i.FileId == 5);
        Assert.Equal(7000000000, mk8.Size);
        Assert.Equal("xci", mk8.FileExtension);
        Assert.Equal("MK8 [0100152000022000][v0].xci", mk8.SourceFileName);
    }

    [Fact]
    public async Task Prefers_remote_sections_endpoint_when_available()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(SectionsJson));
        var result = await TestData.Client(handler).GetCatalogAsync(TestData.Connection, default);

        Assert.Equal("/api/remote/sections", result.Endpoint);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Does_not_fall_back_on_authentication_errors()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));

        Assert.Equal(AeroFoilErrorKind.InvalidCredentials, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("Incorrect password for user \"alice\".", AeroFoilErrorKind.InvalidCredentials)]
    [InlineData("Unknown user \"bob\".", AeroFoilErrorKind.InvalidCredentials)]
    [InlineData("Shop requires authentication.", AeroFoilErrorKind.InvalidCredentials)]
    [InlineData("User \"alice\" does not have access to the shop.", AeroFoilErrorKind.AccessDenied)]
    [InlineData("Too many failed attempts from this client IP. Try again in 30s.", AeroFoilErrorKind.RateLimited)]
    [InlineData("Account is frozen.", AeroFoilErrorKind.AccountFrozen)]
    public async Task Detects_aerofoil_error_payloads_returned_with_http_200(string message, AeroFoilErrorKind expected)
    {
        var json = $$"""{"error": {{System.Text.Json.JsonSerializer.Serialize(message)}}}""";
        var handler = new FakeHandler(_ => FakeHandler.Json(json));

        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));
        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Reports_external_client_restriction_clearly()
    {
        var handler = new FakeHandler(_ =>
            FakeHandler.Json("""{"error": "External access is restricted to Tinfoil/CyberFoil clients."}""", HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));
        Assert.Equal(AeroFoilErrorKind.ClientBlocked, ex.Kind);
        Assert.Contains("does not impersonate", ex.Message);
    }

    [Fact]
    public async Task Never_sends_a_tinfoil_or_cyberfoil_user_agent()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(SectionsJson));
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EdenGameLibraryManager/1.0.0");
        var client = new EdenGameLibraryManager.Infrastructure.AeroFoil.AeroFoilClient(http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EdenGameLibraryManager.Infrastructure.AeroFoil.AeroFoilClient>.Instance);

        await client.GetCatalogAsync(TestData.Connection, default);

        var ua = handler.Requests[0].Headers.UserAgent.ToString() + http.DefaultRequestHeaders.UserAgent;
        Assert.DoesNotContain("tinfoil", ua, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cyberfoil", ua, StringComparison.OrdinalIgnoreCase);
        Assert.False(handler.Requests[0].Headers.Contains("Hauth"));
    }

    [Fact]
    public async Task Rejects_encrypted_catalogues()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("TINFOIL\x00\x01binary"u8.ToArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
            },
        });

        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));
        Assert.Equal(AeroFoilErrorKind.EncryptedCatalog, ex.Kind);
    }

    [Fact]
    public async Task Frozen_placeholder_catalogue_is_reported_as_frozen_account()
    {
        const string frozen = """
            {"sections":[{"id":"all","title":"All","items":[{"name":"Account frozen","title_id":"0000000000000000",
             "app_type":"BASE","url":"/api/frozen/notice#frozen.txt","size":1,"file_id":0}]}]}
            """;
        var handler = new FakeHandler(_ => FakeHandler.Json(frozen));
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));
        Assert.Equal(AeroFoilErrorKind.AccountFrozen, ex.Kind);
    }

    [Fact]
    public async Task Truncated_all_section_is_rerequested_with_limit()
    {
        const string truncated = """
            {"sections":[{"id":"all","title":"All","total":3,"truncated":true,"items":[
              {"name":"A","app_id":"0100000000010000","app_type":"BASE","url":"/api/get_game/1#a.nsp","size":1,"file_id":1}]}]}
            """;
        const string full = """
            {"sections":[{"id":"all","title":"All","total":3,"truncated":false,"items":[
              {"name":"A","app_id":"0100000000010000","app_type":"BASE","url":"/api/get_game/1#a.nsp","size":1,"file_id":1},
              {"name":"B","app_id":"0100000000020000","app_type":"BASE","url":"/api/get_game/2#b.nsp","size":1,"file_id":2},
              {"name":"C","app_id":"0100000000030000","app_type":"BASE","url":"/api/get_game/3#c.nsp","size":1,"file_id":3}]}]}
            """;
        var handler = new FakeHandler(r => r.RequestUri!.Query.Contains("limit=3") ? FakeHandler.Json(full) : FakeHandler.Json(truncated));

        var result = await TestData.Client(handler).GetCatalogAsync(TestData.Connection, default);
        Assert.Equal(3, result.Items.Count);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public async Task Warns_when_completeness_cannot_be_confirmed()
    {
        const string noAll = """
            {"sections":[{"id":"new","title":"New","items":[
              {"name":"A","app_id":"0100000000010000","app_type":"BASE","url":"/api/get_game/1#a.nsp","size":1,"file_id":1}]}]}
            """;
        var handler = new FakeHandler(_ => FakeHandler.Json(noAll));
        var result = await TestData.Client(handler).GetCatalogAsync(TestData.Connection, default);

        Assert.False(result.IsComplete);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task Handles_missing_optional_metadata()
    {
        const string sparse = """
            {"sections":[{"id":"all","title":"All","items":[
              {"url":"/api/get_game/9#Mystery [0100ABCD00010000][v0].nsz"},
              {"name":"No url"},
              {"name":"Odd types","url":"/api/get_game/10","size":null,"app_version":null,"title_id":123,"category":["RPG","Action"]}]}]}
            """;
        var handler = new FakeHandler(_ => FakeHandler.Json(sparse));
        var result = await TestData.Client(handler).GetCatalogAsync(TestData.Connection, default);

        Assert.Equal(2, result.Items.Count);
        var mystery = result.Items[0];
        Assert.Equal("Mystery [0100ABCD00010000][v0].nsz", mystery.Name);
        Assert.Equal(0, mystery.Size);
        Assert.Null(mystery.IconUrl);
        Assert.Equal("RPG, Action", result.Items[1].Category);
        Assert.Contains(result.Warnings, w => w.Contains("ignored"));
    }

    [Fact]
    public async Task Sends_basic_auth_only_to_configured_origin_and_refuses_cross_origin_redirects()
    {
        var handler = new FakeHandler(r => r.RequestUri!.Host == "aerofoil.example.com"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example.net/steal") } }
            : FakeHandler.Json(SectionsJson));

        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));

        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, ex.Kind);
        Assert.All(handler.Requests, r => Assert.Equal("aerofoil.example.com", r.RequestUri!.Host));
        Assert.Equal("Basic", handler.Requests[0].Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Follows_same_origin_redirects()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/api/remote/sections"
            ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("/api/shop/sections", UriKind.Relative) } }
            : FakeHandler.Json(SectionsJson));

        var result = await TestData.Client(handler).GetCatalogAsync(TestData.Connection, default);
        Assert.Equal(4, result.Items.Count);
        Assert.All(handler.Requests, r => Assert.NotNull(r.Headers.Authorization));
    }

    [Fact]
    public async Task Network_failures_are_classified_as_transient_connectivity_errors()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("No such host is known."));
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));

        Assert.Equal(AeroFoilErrorKind.Network, ex.Kind);
        Assert.True(ex.IsTransient);
        Assert.True(ex.IsConnectivityFailure);
    }

    [Fact]
    public async Task Server_errors_are_transient()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() => TestData.Client(handler).GetCatalogAsync(TestData.Connection, default));
        Assert.Equal(AeroFoilErrorKind.ServerError, ex.Kind);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Parses_title_info()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""
            {"success": true, "title_id": "0100152000022000", "description": "Race!",
             "screenshots": ["/api/shop/screenshot/0100152000022000/0", "/api/shop/screenshot/0100152000022000/1"]}
            """));
        var details = await TestData.Client(handler).GetTitleInfoAsync(TestData.Connection, "0100152000022000", default);

        Assert.NotNull(details);
        Assert.Equal("Race!", details!.Description);
        Assert.Equal(2, details.ScreenshotUrls.Count);
        Assert.Equal("/api/shop/title-info/0100152000022000", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Restricted_title_info_returns_null()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json("""{"success": false, "error": "Title is restricted."}""", HttpStatusCode.Forbidden));
        Assert.Null(await TestData.Client(handler).GetTitleInfoAsync(TestData.Connection, "0100152000022000", default));
    }

    [Fact]
    public async Task Artwork_from_other_origins_is_refused()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var ex = await Assert.ThrowsAsync<AeroFoilException>(() =>
            TestData.Client(handler).GetResourceAsync(TestData.Connection, "https://cdn.example.org/icon.png", 1000, default));
        Assert.Equal(AeroFoilErrorKind.UntrustedUrl, ex.Kind);
        Assert.Empty(handler.Requests);
    }
}
