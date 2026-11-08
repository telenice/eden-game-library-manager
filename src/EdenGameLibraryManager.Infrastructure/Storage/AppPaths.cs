namespace EdenGameLibraryManager.Infrastructure.Storage;

/// <summary>
/// Per-user application data locations, by default under %LOCALAPPDATA%\EdenGameLibraryManager.
/// Partial downloads live here, outside the games directory the emulator scans.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "EdenGameLibraryManager";

    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);
    }

    public string Root { get; }
    public string DownloadsDirectory => Path.Combine(Root, "Downloads");
    public string ArtworkDirectory => Path.Combine(Root, "Artwork");
    public string LogsDirectory => Path.Combine(Root, "Logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    /// <summary>Single credentials file written by version 1.0.0 (migrated on first read).</summary>
    public string LegacyCredentialsFile => Path.Combine(Root, "credentials.dat");
    public string DatabaseFile => Path.Combine(Root, "library.db");
    public string CatalogCacheDirectory => Path.Combine(Root, "Catalogs");

    public string CredentialsFileFor(string profileId) => Path.Combine(Root, $"credentials-{SafeName(profileId)}.dat");

    /// <summary>Each server keeps its own offline catalogue so switching profiles doesn't discard it.</summary>
    public string CatalogCacheFileFor(string serverKey)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(serverKey)));
        return Path.Combine(CatalogCacheDirectory, hash[..24].ToLowerInvariant() + ".json");
    }

    private static string SafeName(string value) =>
        new(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DownloadsDirectory);
        Directory.CreateDirectory(ArtworkDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CatalogCacheDirectory);
    }
}
