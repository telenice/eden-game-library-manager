using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using EdenGameLibraryManager.Infrastructure.Storage;

namespace EdenGameLibraryManager.App.ViewModels;

public sealed record ThemeOption(AppTheme Theme, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ICredentialStore _credentials;
    private readonly IAeroFoilClient _client;
    private readonly LibraryCoordinator _coordinator;
    private readonly IDialogService _dialogs;
    private readonly AppPaths _paths;
    private readonly ILocalLibraryService _library;
    private readonly HashSet<string> _removedProfileIds = [];

    public SettingsViewModel(
        ISettingsService settings,
        ICredentialStore credentials,
        IAeroFoilClient client,
        LibraryCoordinator coordinator,
        IDialogService dialogs,
        AppPaths paths,
        ILocalLibraryService library)
    {
        _library = library;
        _settings = settings;
        _credentials = credentials;
        _client = client;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _paths = paths;
        _selectedTheme = ThemeOptions[0];
        Load(settings.Current);
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(AppTheme.Dark, "Dark"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.System, "Follow system"),
    ];

    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4];

    /// <summary>Server profiles being edited; changes apply when Settings are saved.</summary>
    public ObservableCollection<ConnectionFormViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProfile))]
    [NotifyCanExecuteChangedFor(nameof(RemoveProfileCommand))]
    private ConnectionFormViewModel? _selectedProfile;

    [ObservableProperty] private string _gamesDirectory = string.Empty;
    [ObservableProperty] private decimal _cacheLimitGb;
    [ObservableProperty] private bool _autoCleanup;
    [ObservableProperty] private bool _organizeByGame;
    [ObservableProperty] private bool _includeUpdatesAndDlc;
    [ObservableProperty] private bool _isOrganizing;
    [ObservableProperty] private int _maxConcurrentDownloads = 1;
    [ObservableProperty] private bool _autoResumeDownloads;
    [ObservableProperty] private bool _autoRefreshOnStartup;
    [ObservableProperty] private decimal _refreshIntervalMinutes;
    [ObservableProperty] private decimal _speedLimitKbps;
    [ObservableProperty] private ThemeOption _selectedTheme;
    [ObservableProperty] private string? _statusMessage;

    public string DataFolder => _paths.Root;
    public bool HasSelectedProfile => SelectedProfile is not null;

    public void Load(AppSettings s)
    {
        _removedProfileIds.Clear();
        Profiles.Clear();
        foreach (var profile in s.Profiles)
            Profiles.Add(new ConnectionFormViewModel(_client, _credentials, profile));
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == s.ActiveProfile?.Id) ?? Profiles.FirstOrDefault();

        GamesDirectory = s.GamesDirectory;
        CacheLimitGb = (decimal)s.CacheLimitGb;
        AutoCleanup = s.AutoCleanup;
        OrganizeByGame = s.OrganizeByGame;
        IncludeUpdatesAndDlc = s.IncludeUpdatesAndDlc;
        MaxConcurrentDownloads = s.MaxConcurrentDownloads;
        AutoResumeDownloads = s.AutoResumeDownloads;
        AutoRefreshOnStartup = s.AutoRefreshOnStartup;
        RefreshIntervalMinutes = s.RefreshIntervalMinutes;
        SpeedLimitKbps = s.SpeedLimitKbps;
        SelectedTheme = ThemeOptions.FirstOrDefault(t => t.Theme == s.Theme) ?? ThemeOptions[0];
        StatusMessage = null;
    }

    partial void OnSelectedThemeChanged(ThemeOption value) => App.ApplyTheme(value.Theme);

    [RelayCommand]
    private void AddProfile()
    {
        var profile = new ConnectionFormViewModel(_client, _credentials, new ServerProfile { Id = ServerProfile.NewId(), Name = "New server" });
        Profiles.Add(profile);
        SelectedProfile = profile;
        StatusMessage = "Fill in the new server, then save.";
    }

    private bool CanRemoveProfile() => SelectedProfile is not null && Profiles.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemoveProfile))]
    private async Task RemoveProfileAsync()
    {
        var profile = SelectedProfile;
        if (profile is null) return;
        if (!await _dialogs.ConfirmAsync("Remove server profile",
                $"Remove \"{profile.DisplayName}\"? Its saved password is deleted when you save. Downloaded games are kept.",
                "Remove", destructive: true))
            return;

        _removedProfileIds.Add(profile.Id);
        var index = Profiles.IndexOf(profile);
        Profiles.Remove(profile);
        SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
        RemoveProfileCommand.NotifyCanExecuteChanged();
        StatusMessage = "Profile removed. Save to apply.";
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose your games directory", GamesDirectory);
        if (folder is not null) GamesDirectory = folder;
    }

    [RelayCommand]
    private void OpenDataFolder() => PlatformShell.RevealInFileManager(_paths.Root);

    /// <summary>
    /// Moves games this app downloaded into "&lt;Game name&gt; [TitleID]" folders. Manually added files
    /// are never moved.
    /// </summary>
    [RelayCommand]
    private async Task OrganizeExistingAsync()
    {
        var directory = _settings.Current.GamesDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            StatusMessage = "Choose and save a games directory first.";
            return;
        }

        var all = _library.Games;
        var bases = all.Where(g => g.ContentType == ContentType.Base)
            .GroupBy(GameGrouping.GroupKey)
            .ToDictionary(g => g.Key, g => g.First());

        string TitleFor(LocalGame game)
        {
            var remote = _coordinator.FindRemoteFor(game);
            if (!string.IsNullOrWhiteSpace(remote?.TitleName)) return remote.TitleName!;
            if (bases.TryGetValue(GameGrouping.GroupKey(game), out var baseGame))
                return _coordinator.FindRemoteFor(baseGame)?.Name ?? baseGame.Name ?? baseGame.DisplayName;
            return remote?.Name ?? game.Name ?? game.DisplayName;
        }

        var moves = all
            .Where(g => g.IsManaged)
            .Select(g => (Game: g, Target: Path.Combine(directory, GameGrouping.FolderName(TitleFor(g), GameGrouping.GroupKey(g)))))
            .Where(m => !string.Equals(Path.GetDirectoryName(Path.GetFullPath(m.Game.LocalPath)), Path.GetFullPath(m.Target),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (moves.Count == 0)
        {
            StatusMessage = "Downloaded games are already organised into per-game folders.";
            return;
        }
        if (!await _dialogs.ConfirmAsync("Organise downloads",
                $"Move {moves.Count} file(s) downloaded by this app into a folder per game inside {directory}?\n\n" +
                "Files you added yourself are not moved. Make sure Eden scans subfolders of this directory " +
                "(right-click the directory in Eden's game list and enable \"Scan Subfolders\").",
                "Move files"))
            return;

        IsOrganizing = true;
        var errors = new List<string>();
        try
        {
            foreach (var (game, target) in moves)
            {
                try
                {
                    await Task.Run(() => _library.MoveAsync(game, target, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    errors.Add($"{game.FileName}: {ex.Message}");
                }
            }
            await _coordinator.RescanAsync();
        }
        finally
        {
            IsOrganizing = false;
        }

        StatusMessage = errors.Count == 0
            ? $"Moved {moves.Count} file(s) into per-game folders."
            : $"Moved {moves.Count - errors.Count} of {moves.Count} file(s).";
        if (errors.Count > 0)
            await _dialogs.ShowMessageAsync("Some files were not moved", string.Join(Environment.NewLine, errors));
    }

    [RelayCommand]
    private void Revert() => Load(_settings.Current);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Profiles.Count == 0)
        {
            StatusMessage = "Add at least one AeroFoil server.";
            return;
        }

        var profiles = new List<ServerProfile>();
        foreach (var form in Profiles)
        {
            var connection = form.TryBuildConnection();
            if (connection is null)
            {
                SelectedProfile = form;
                StatusMessage = $"{form.DisplayName}: {form.TestMessage}";
                return;
            }
            profiles.Add(form.ToProfile(connection.BaseUri.ToString()));
        }
        if (string.IsNullOrWhiteSpace(GamesDirectory))
        {
            StatusMessage = "Choose a games directory.";
            return;
        }

        var old = _settings.Current;
        var activeId = profiles.Any(p => p.Id == old.ActiveProfileId) ? old.ActiveProfileId : profiles[0].Id;
        var updated = old with
        {
            Profiles = profiles,
            ActiveProfileId = activeId,
            GamesDirectory = GamesDirectory.Trim(),
            CacheLimitGb = (double)CacheLimitGb,
            AutoCleanup = AutoCleanup,
            OrganizeByGame = OrganizeByGame,
            IncludeUpdatesAndDlc = IncludeUpdatesAndDlc,
            MaxConcurrentDownloads = MaxConcurrentDownloads,
            AutoResumeDownloads = AutoResumeDownloads,
            AutoRefreshOnStartup = AutoRefreshOnStartup,
            RefreshIntervalMinutes = (int)RefreshIntervalMinutes,
            SpeedLimitKbps = (int)SpeedLimitKbps,
            Theme = SelectedTheme.Theme,
        };

        try
        {
            Directory.CreateDirectory(updated.GamesDirectory);
        }
        catch (Exception ex)
        {
            StatusMessage = $"The games directory cannot be used: {ex.Message}";
            return;
        }

        var oldActivePassword = old.ActiveProfile is { } oa ? _credentials.GetPassword(oa.Id) : null;
        foreach (var form in Profiles)
            _credentials.SetPassword(form.Id, form.Password, form.RememberPassword);
        foreach (var id in _removedProfileIds)
            _credentials.Clear(id);
        _removedProfileIds.Clear();

        await _settings.SaveAsync(updated);
        StatusMessage = "Settings saved.";

        var newActive = updated.ActiveProfile;
        var serverChanged = old.ActiveProfile != newActive ||
                            oldActivePassword != (newActive is null ? null : _credentials.GetPassword(newActive.Id));
        if (!string.Equals(old.GamesDirectory, updated.GamesDirectory, StringComparison.OrdinalIgnoreCase))
            await _coordinator.RescanAsync();
        else
            _coordinator.Recompute();
        if (serverChanged) await _coordinator.SwitchProfileAsync();
    }
}
