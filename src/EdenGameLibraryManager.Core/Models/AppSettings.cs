using System.Text.Json.Serialization;

namespace EdenGameLibraryManager.Core.Models;

public enum ViewMode { Grid, List }

public enum AppTheme { Dark, Light, System }

/// <summary>One AeroFoil server the user can switch to. The password lives in the OS secret store, keyed by <see cref="Id"/>.</summary>
public sealed record ServerProfile
{
    public required string Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ServerUrl { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool RememberPassword { get; init; } = true;

    /// <summary>Plain HTTP is only allowed when the user explicitly opts in for a trusted LAN/VPN server.</summary>
    public bool AllowInsecureHttp { get; init; }

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name
        : Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u) ? u.Host
        : string.IsNullOrWhiteSpace(ServerUrl) ? "New server" : ServerUrl;

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Persisted user preferences. Never contains passwords.</summary>
public sealed record AppSettings
{
    public IReadOnlyList<ServerProfile> Profiles { get; init; } = [];
    public string? ActiveProfileId { get; init; }

    /// <summary>Shared by all profiles: the emulator scans a single folder.</summary>
    public string GamesDirectory { get; init; } = string.Empty;

    /// <summary>Maximum managed cache size in GB; 0 means unlimited.</summary>
    public double CacheLimitGb { get; init; }

    public bool AutoCleanup { get; init; }

    /// <summary>Save downloads to "&lt;games dir&gt;/&lt;Game name&gt; [TitleID]/" instead of the root of the games directory.</summary>
    public bool OrganizeByGame { get; init; } = true;

    /// <summary>Downloading a base game also queues its latest update and DLC from the same server.</summary>
    public bool IncludeUpdatesAndDlc { get; init; } = true;
    public int MaxConcurrentDownloads { get; init; } = 1;
    public bool AutoResumeDownloads { get; init; } = true;
    public bool AutoRefreshOnStartup { get; init; } = true;

    /// <summary>Background catalogue refresh interval in minutes; 0 disables it.</summary>
    public int RefreshIntervalMinutes { get; init; } = 30;

    /// <summary>Global download speed limit in KB/s; 0 means unlimited.</summary>
    public int SpeedLimitKbps { get; init; }

    public AppTheme Theme { get; init; } = AppTheme.Dark;
    public ViewMode ViewMode { get; init; } = ViewMode.Grid;
    public bool GroupByGame { get; init; }
    public bool SetupCompleted { get; init; }

    // Single-server fields written by version 1.0.0; migrated into Profiles when settings load.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ServerUrl { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Username { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? RememberPassword { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? AllowInsecureHttp { get; init; }

    [JsonIgnore]
    public ServerProfile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.FirstOrDefault();

    [JsonIgnore]
    public long CacheLimitBytes => CacheLimitGb <= 0 ? 0 : (long)(CacheLimitGb * 1024 * 1024 * 1024);
}
