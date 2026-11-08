using System.Text.Json;
using System.Text.Json.Serialization;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.Infrastructure.Persistence;

/// <summary>JSON settings in the user's app-data folder, written atomically. Never contains passwords.</summary>
public sealed class SettingsService : ISettingsService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _paths;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public SettingsService(AppPaths paths, ILogger<SettingsService> logger)
    {
        _paths = paths;
        _logger = logger;
        _current = Load();
    }

    public AppSettings Current => Volatile.Read(ref _current);

    public event EventHandler<AppSettings>? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.Root);
            var temp = _paths.SettingsFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(normalized, JsonOptions), cancellationToken);
            File.Move(temp, _paths.SettingsFile, overwrite: true);
            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(this, normalized);
    }

    /// <summary>Id given to the profile migrated from 1.0.0's single-server settings.</summary>
    public const string MigratedProfileId = "default";

    internal static AppSettings Normalize(AppSettings s)
    {
        var profiles = (s.Profiles ?? []).ToList();

        // 1.0.0 stored one server at the top level; turn it into the first profile.
        if (profiles.Count == 0 && !string.IsNullOrWhiteSpace(s.ServerUrl))
        {
            profiles.Add(new ServerProfile
            {
                Id = MigratedProfileId,
                Name = "Default",
                ServerUrl = s.ServerUrl,
                Username = s.Username ?? string.Empty,
                RememberPassword = s.RememberPassword ?? true,
                AllowInsecureHttp = s.AllowInsecureHttp ?? false,
            });
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        profiles = profiles
            .Select(p => p with
            {
                Id = string.IsNullOrWhiteSpace(p.Id) || !seen.Add(p.Id) ? ServerProfile.NewId() : p.Id,
                Name = (p.Name ?? string.Empty).Trim(),
                ServerUrl = (p.ServerUrl ?? string.Empty).Trim(),
                Username = (p.Username ?? string.Empty).Trim(),
            })
            .ToList();

        var activeId = profiles.Any(p => p.Id == s.ActiveProfileId) ? s.ActiveProfileId : profiles.FirstOrDefault()?.Id;

        return s with
        {
            Profiles = profiles,
            ActiveProfileId = activeId,
            ServerUrl = null,
            Username = null,
            RememberPassword = null,
            AllowInsecureHttp = null,
            GamesDirectory = (s.GamesDirectory ?? string.Empty).Trim(),
            CacheLimitGb = Math.Max(0, s.CacheLimitGb),
            MaxConcurrentDownloads = Math.Clamp(s.MaxConcurrentDownloads, 1, 4),
            RefreshIntervalMinutes = Math.Clamp(s.RefreshIntervalMinutes, 0, 24 * 60),
            SpeedLimitKbps = Math.Max(0, s.SpeedLimitKbps),
        };
    }

    private AppSettings Load()
    {
        if (!File.Exists(_paths.SettingsFile)) return new AppSettings();
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_paths.SettingsFile), JsonOptions);
            return Normalize(loaded ?? new AppSettings());
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogError(ex, "Settings file is unreadable; using defaults and keeping a backup");
            try
            {
                File.Copy(_paths.SettingsFile, _paths.SettingsFile + ".corrupt", overwrite: true);
            }
            catch (IOException)
            {
            }
            return new AppSettings();
        }
    }
}
