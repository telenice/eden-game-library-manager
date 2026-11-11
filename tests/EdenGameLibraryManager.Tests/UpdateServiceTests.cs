using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.Storage;
using EdenGameLibraryManager.Infrastructure.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace EdenGameLibraryManager.Tests;

public class UpdateServiceTests
{
    private const string PackageUrl = "https://github.com/telenice/eden-game-library-manager/releases/download/v1.0.7/EdenGameLibraryManager-1.0.7-win-x64.zip";

    private static string Release(string tag = "v1.0.7", bool prerelease = false, bool withSignature = true) => $$"""
        {"tag_name":"{{tag}}","draft":false,"prerelease":{{(prerelease ? "true" : "false")}},
         "html_url":"https://github.com/telenice/eden-game-library-manager/releases/tag/{{tag}}","body":"Notes","published_at":"2026-11-12T10:00:00Z",
         "assets":[
           {"name":"EdenGameLibraryManager-1.0.7-win-arm64.zip","size":10,"browser_download_url":"https://github.com/x/arm64.zip"},
           {"name":"EdenGameLibraryManager-1.0.7-win-x64.zip","size":10,"browser_download_url":"{{PackageUrl}}"},
           {{(withSignature ? $$"""{"name":"EdenGameLibraryManager-1.0.7-win-x64.zip.sig","size":72,"browser_download_url":"{{PackageUrl}}.sig"},""" : "")}}
           {"name":"EdenGameLibraryManager-1.0.7-osx-arm64.dmg","size":10,"browser_download_url":"https://github.com/x/a.dmg"}]}
        """;

    private static UpdateService Service(FakeHandler handler, string root, string? exe = null, bool windows = true, string? publicKey = null) =>
        new(new HttpClient(handler), new AppPaths(root), NullLogger<UpdateService>.Instance)
        {
            CurrentVersion = new Version(1, 0, 6),
            ExecutablePath = exe,
            IsPublishedBuild = true,
            IsWindows = windows,
            RuntimeId = "win-x64",
            PublicKeyPem = publicKey ?? TestKey.ExportSubjectPublicKeyInfoPem(),
        };

    private static readonly ECDsa TestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Theory]
    [InlineData("v1.0.7", "1.0.7")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2.0.0-beta1", null)]
    [InlineData("latest", null)]
    [InlineData(null, null)]
    public void Tags_are_parsed_as_versions(string? tag, string? expected) =>
        Assert.Equal(expected, UpdateService.ParseVersion(tag)?.ToString());

    [Fact]
    public void Newer_release_picks_this_machines_zip_and_its_signature()
    {
        using var temp = new TempDirectory();
        using var doc = JsonDocument.Parse(Release());
        var update = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), temp.Path).ParseRelease(doc.RootElement)!;

        Assert.Equal(new Version(1, 0, 7), update.Version);
        Assert.Equal(PackageUrl, update.PackageUrl!.ToString());
        Assert.Equal(PackageUrl + ".sig", update.SignatureUrl!.ToString());
        Assert.True(update.CanInstallAutomatically);
        Assert.Equal("https://github.com/telenice/eden-game-library-manager/releases/tag/v1.0.7", update.ReleasePage.ToString());
    }

    [Theory]
    [InlineData("v1.0.6", false)]
    [InlineData("v1.0.5", false)]
    [InlineData("v1.0.7", true)]
    public void Same_older_and_prerelease_versions_are_not_offered(string tag, bool prerelease)
    {
        using var temp = new TempDirectory();
        using var doc = JsonDocument.Parse(Release(tag, prerelease));
        Assert.Null(Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), temp.Path).ParseRelease(doc.RootElement));
    }

    [Fact]
    public void Unsigned_release_or_another_os_is_offered_as_a_download_only()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var unsigned = JsonDocument.Parse(Release(withSignature: false));
        using var signed = JsonDocument.Parse(Release());

        Assert.False(Service(handler, temp.Path).ParseRelease(unsigned.RootElement)!.CanInstallAutomatically);
        var mac = Service(handler, temp.Path, windows: false).ParseRelease(signed.RootElement)!;
        Assert.Null(mac.PackageUrl);
        Assert.False(mac.CanInstallAutomatically);
    }

    [Fact]
    public async Task Check_reads_the_latest_release_and_treats_no_release_as_up_to_date()
    {
        using var temp = new TempDirectory();
        var handler = new FakeHandler(_ => FakeHandler.Json(Release()));
        var update = await Service(handler, temp.Path).CheckAsync(default);

        Assert.Equal(new Version(1, 0, 7), update!.Version);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/telenice/eden-game-library-manager/releases/latest", request.RequestUri!.ToString());

        var none = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), temp.Path);
        Assert.Null(await none.CheckAsync(default));
    }

    private static byte[] Package(byte[] exe)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("EdenGameLibraryManager-1.0.7-win-x64/EdenGameLibraryManager.exe").Open()) s.Write(exe);
            using (var s = zip.CreateEntry("EdenGameLibraryManager-1.0.7-win-x64/README.txt").Open()) s.Write("readme"u8);
        }
        return ms.ToArray();
    }

    private static (UpdateService Service, string Exe, string Folder, AppUpdate Update) Install(TempDirectory temp, byte[] package, byte[] signature)
    {
        var folder = temp.Combine("Eden");
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "EdenGameLibraryManager.exe");
        File.WriteAllText(exe, "old app");
        File.WriteAllText(Path.Combine(folder, "eden.exe"), "the emulator");

        var handler = new FakeHandler(r => r.RequestUri!.ToString().EndsWith(".sig", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(signature) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
        var service = Service(handler, temp.Combine("appdata"), exe);
        using var doc = JsonDocument.Parse(Release());
        return (service, exe, folder, service.ParseRelease(doc.RootElement)!);
    }

    [Fact]
    public async Task Install_replaces_only_this_apps_exe_after_checking_the_signature()
    {
        using var temp = new TempDirectory();
        var package = Package(Encoding.UTF8.GetBytes("new app"));
        var (service, exe, folder, update) = Install(temp, package, TestKey.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.True(service.CanInstallInPlace);

        var progress = new List<double>();
        var restart = await service.InstallAsync(update, new SyncProgress(progress.Add), default);

        Assert.Equal(exe, restart);
        Assert.Equal("new app", await File.ReadAllTextAsync(exe));
        Assert.Equal("old app", await File.ReadAllTextAsync(exe + ".old"));
        Assert.Equal("the emulator", await File.ReadAllTextAsync(Path.Combine(folder, "eden.exe")));
        Assert.False(File.Exists(Path.Combine(folder, "README.txt")));
        Assert.Equal(1.0, progress.Last());
        Assert.False(Directory.Exists(temp.Combine("appdata", "Updates")));

        await service.CleanUpAsync();
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task Package_signed_with_another_key_is_refused_and_nothing_changes()
    {
        using var temp = new TempDirectory();
        var package = Package(Encoding.UTF8.GetBytes("tampered app"));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (service, exe, _, update) = Install(temp, package, otherKey.SignData(package, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        var ex = await Assert.ThrowsAsync<UpdateException>(() => service.InstallAsync(update, null, default));

        Assert.Contains("signature", ex.Message);
        Assert.Equal("old app", await File.ReadAllTextAsync(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public async Task Garbage_signature_is_refused()
    {
        using var temp = new TempDirectory();
        var (service, exe, _, update) = Install(temp, Package("x"u8.ToArray()), "not a signature"u8.ToArray());
        await Assert.ThrowsAsync<UpdateException>(() => service.InstallAsync(update, null, default));
        Assert.Equal("old app", await File.ReadAllTextAsync(exe));
    }

    [Fact]
    public void Embedded_public_key_is_a_valid_p256_key()
    {
        using var temp = new TempDirectory();
        var service = new UpdateService(new HttpClient(), new AppPaths(temp.Path), NullLogger<UpdateService>.Instance);
        using var key = ECDsa.Create();
        key.ImportFromPem(service.PublicKeyPem);
        Assert.Equal(256, key.KeySize);
        Assert.False(service.CanInstallInPlace); // a test run is not a published build
    }

    /// <summary>Progress&lt;T&gt; posts to the synchronization context; this reports inline.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
