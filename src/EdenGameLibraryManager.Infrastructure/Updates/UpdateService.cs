using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.AeroFoil;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Updates;

/// <summary>
/// Self-update from GitHub Releases. The latest release (drafts and pre-releases excluded) is read
/// from the public API; on Windows the matching <c>-win-x64.zip</c>/<c>-win-arm64.zip</c> is downloaded
/// with its <c>.sig</c>, which must be an ECDSA P-256 / SHA-256 signature by the release key whose
/// public half is embedded in this assembly. Only this app's own executable is replaced: the running
/// file is renamed to <c>.old</c> (Windows allows renaming a running exe) and the new one moved into
/// place. Nothing else in the folder is touched, so an exe kept next to Eden's own files is safe.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    public const string Repository = "telenice/eden-game-library-manager";
    private const string ExecutableName = "EdenGameLibraryManager.exe";
    private const long MaxPackageBytes = 512L * 1024 * 1024;
    private const long MaxSignatureBytes = 4096;

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(HttpClient http, AppPaths paths, ILogger<UpdateService> logger)
    {
        _http = http;
        _paths = paths;
        _logger = logger;
        var version = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        CurrentVersion = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public Version CurrentVersion { get; internal init; }

    /// <summary>The running executable; overridable for tests.</summary>
    internal string? ExecutablePath { get; init; } = Environment.ProcessPath;

    /// <summary>True for a published single-file build (a development build runs from bin\ and must never replace itself).</summary>
    internal bool IsPublishedBuild { get; init; } = string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);

    internal bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    internal string RuntimeId { get; init; } = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    internal string PublicKeyPem { get; init; } = ReadEmbeddedPublicKey();

    internal TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static HttpClient CreateHttpClient(string userAgent)
    {
        // Release assets redirect from github.com to GitHub's download host. The package's signature
        // is what's trusted, not the host it arrives from; .NET never follows HTTPS -> HTTP redirects.
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            UseCookies = false,
        })
        { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return http;
    }

    public bool CanInstallInPlace => IsWindows && IsPublishedBuild && ExecutablePath is { } exe &&
                                     exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsWritable(Path.GetDirectoryName(exe)!);

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        byte[] body;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no published release yet
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered HTTP {(int)response.StatusCode} when checking for updates.");
            body = await AeroFoilResponses.ReadLimitedAsync(response.Content, 4 * 1024 * 1024, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Could not reach GitHub to check for updates: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("GitHub did not respond in time.");
        }
        catch (AeroFoilException ex)
        {
            throw new UpdateException(ex.Message, ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseRelease(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new UpdateException("GitHub returned an unreadable release.", ex);
        }
    }

    internal AppUpdate? ParseRelease(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = String(release, "tag_name");
        if (ParseVersion(tag) is not { } version || version <= CurrentVersion) return null;
        if (!Uri.TryCreate(String(release, "html_url"), UriKind.Absolute, out var page) || page.Scheme != Uri.UriSchemeHttps)
            page = new Uri($"https://github.com/{Repository}/releases/tag/{Uri.EscapeDataString(tag!)}");

        var assets = release.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
                .Select(x => (Name: String(x, "name"), Url: String(x, "browser_download_url"), Size: x.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0))
                .Where(x => x.Name is not null && Uri.TryCreate(x.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps)
                .ToList()
            : [];

        var package = IsWindows
            ? assets.FirstOrDefault(x => x.Name!.EndsWith($"-{RuntimeId}.zip", StringComparison.OrdinalIgnoreCase))
            : default;
        var signature = package.Name is null
            ? default
            : assets.FirstOrDefault(x => string.Equals(x.Name, package.Name + ".sig", StringComparison.OrdinalIgnoreCase));

        return new AppUpdate
        {
            Version = version,
            Tag = tag!,
            ReleasePage = page,
            Notes = String(release, "body"),
            PublishedAt = DateTimeOffset.TryParse(String(release, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null,
            PackageUrl = package.Url is null ? null : new Uri(package.Url),
            PackageName = package.Name,
            PackageSize = package.Size,
            SignatureUrl = signature.Url is null ? null : new Uri(signature.Url),
        };
    }

    /// <summary>"v1.2.3" -> 1.2.3. Pre-release suffixes ("-beta1") are rejected.</summary>
    internal static Version? ParseVersion(string? tag)
    {
        var text = tag?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(text) || text.Contains('-') || !Version.TryParse(text, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public async Task<string> InstallAsync(AppUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!CanInstallInPlace)
            throw new UpdateException("This copy of the app can't update itself. Download the new version from the release page.");
        if (!update.CanInstallAutomatically)
            throw new UpdateException($"Version {update.Version} has no signed Windows package, so it can't be installed automatically. Download it from the release page.");

        var exe = ExecutablePath!;
        var work = Path.Combine(_paths.Root, "Updates");
        TryDeleteDirectory(work);
        Directory.CreateDirectory(work);
        var package = Path.Combine(work, "package.zip");
        var staged = Path.Combine(work, ExecutableName);

        try
        {
            _logger.LogInformation("Downloading update {Version} ({Package})", update.Version, update.PackageName);
            var signature = await DownloadBytesAsync(update.SignatureUrl!, MaxSignatureBytes, cancellationToken);
            await DownloadFileAsync(update.PackageUrl!, package, update.PackageSize, progress, cancellationToken);

            if (!VerifySignature(package, signature))
                throw new UpdateException("The downloaded update's signature doesn't match the release key, so it was not installed.");

            ExtractExecutable(package, staged);
            Swap(exe, staged);
            _logger.LogInformation("Update {Version} installed; restart pending", update.Version);
            return exe;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            throw new UpdateException($"The update could not be installed: {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("Downloading the update stalled. Try again later.");
        }
        finally
        {
            TryDeleteDirectory(work);
        }
    }

    public Task CleanUpAsync() => Task.Run(async () =>
    {
        TryDeleteDirectory(Path.Combine(_paths.Root, "Updates"));
        if (ExecutablePath is not { } exe) return;
        var old = exe + ".old";
        // The previous process may still be exiting and holding the file; give it a little time.
        for (var attempt = 0; attempt < 10 && File.Exists(old); attempt++)
        {
            try
            {
                File.Delete(old);
                _logger.LogInformation("Removed the previous version's executable");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    });

    internal bool VerifySignature(string file, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(PublicKeyPem);
        byte[] hash;
        using (var stream = File.OpenRead(file))
            hash = SHA256.HashData(stream);
        try
        {
            // openssl dgst -sign writes a DER-encoded signature.
            return key.VerifyHash(hash, signature, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Extracts only this app's executable from the package (it sits in the zip's top folder).</summary>
    internal static void ExtractExecutable(string package, string destination)
    {
        using var zip = ZipFile.OpenRead(package);
        var entries = zip.Entries
            .Where(e => string.Equals(e.Name, ExecutableName, StringComparison.OrdinalIgnoreCase) && e.FullName.Count(c => c is '/' or '\\') <= 1)
            .ToList();
        if (entries.Count != 1)
            throw new UpdateException($"The update package doesn't contain exactly one {ExecutableName}.");
        if (entries[0].Length is <= 0 or > MaxPackageBytes)
            throw new UpdateException($"The update package's {ExecutableName} has an unexpected size.");
        entries[0].ExtractToFile(destination, overwrite: true);
    }

    /// <summary>Renames the running exe to ".old" and moves the new one into place, undoing the rename on failure.</summary>
    internal static void Swap(string exe, string staged)
    {
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    private async Task<byte[]> DownloadBytesAsync(Uri url, long maxBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered HTTP {(int)response.StatusCode} for the update signature.");
        try
        {
            return await AeroFoilResponses.ReadLimitedAsync(response.Content, maxBytes, timeout.Token);
        }
        catch (AeroFoilException)
        {
            throw new UpdateException("The update signature is unexpectedly large.");
        }
    }

    private async Task DownloadFileAsync(Uri url, string path, long expectedSize, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered HTTP {(int)response.StatusCode} for the update package.");
        var total = response.Content.Headers.ContentLength ?? expectedSize;
        if (total > MaxPackageBytes) throw new UpdateException("The update package is unexpectedly large.");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        var buffer = new byte[1 << 16];
        long written = 0;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(TimeSpan.FromSeconds(60));
            var read = await source.ReadAsync(buffer, stall.Token);
            if (read == 0) break;
            written += read;
            if (written > MaxPackageBytes) throw new UpdateException("The update package is unexpectedly large.");
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            if (total > 0) progress?.Report(Math.Min(1, (double)written / total));
        }
        if (total > 0 && written != total)
            throw new UpdateException($"The update download ended early ({written:N0} of {total:N0} bytes).");
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".egl-write-test-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string ReadEmbeddedPublicKey()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("EdenGameLibraryManager.Infrastructure.Updates.update-signing-public-key.pem")
                           ?? throw new InvalidOperationException("The update signing public key is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
