using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace EdenGameLibraryManager.App.ViewModels;

public enum Page
{
    Available,
    Downloaded,
    Downloads,
    Settings,
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly ISettingsService _settings;
    private readonly LibraryCoordinator _coordinator;
    private readonly IDownloadManager _downloads;
    private DispatcherTimer? _refreshTimer;
    private bool _started;

    public MainWindowViewModel(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<ISettingsService>();
        _coordinator = services.GetRequiredService<LibraryCoordinator>();
        _downloads = services.GetRequiredService<IDownloadManager>();

        Available = services.GetRequiredService<AvailableGamesViewModel>();
        Downloaded = services.GetRequiredService<DownloadedGamesViewModel>();
        Downloads = services.GetRequiredService<DownloadsViewModel>();
        Settings = services.GetRequiredService<SettingsViewModel>();

        _coordinator.Changed += (_, _) => UpdateStatus();
        Downloads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DownloadsViewModel.ActiveCount)) OnPropertyChanged(nameof(DownloadsBadge));
        };
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ConfigureRefreshTimer();
            RefreshProfiles();
        });

        _currentPage = Available;
        RefreshProfiles();
        if (!_settings.Current.SetupCompleted)
        {
            Setup = services.GetRequiredService<SetupViewModel>();
            Setup.Completed += async (_, _) =>
            {
                Setup = null;
                Settings.Load(_settings.Current);
                await StartAsync();
            };
        }
    }

    public AvailableGamesViewModel Available { get; }
    public DownloadedGamesViewModel Downloaded { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupMode))]
    private SetupViewModel? _setup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailablePage), nameof(IsDownloadedPage), nameof(IsDownloadsPage), nameof(IsSettingsPage))]
    private ViewModelBase _currentPage;

    [ObservableProperty] private string _connectionText = "Starting…";
    [ObservableProperty] private ConnectionState _connectionState = ConnectionState.NotConfigured;
    [ObservableProperty] private string _storageText = string.Empty;
    [ObservableProperty] private string? _startupError;

    /// <summary>Server profiles for the sidebar switcher.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ServerProfile> Profiles { get; } = [];

    [ObservableProperty] private ServerProfile? _activeProfile;

    private bool _syncingProfiles;

    public bool HasMultipleProfiles => Profiles.Count > 1;

    private void RefreshProfiles()
    {
        _syncingProfiles = true;
        try
        {
            var current = _settings.Current;
            if (!Profiles.SequenceEqual(current.Profiles))
            {
                Profiles.Clear();
                foreach (var p in current.Profiles) Profiles.Add(p);
            }
            ActiveProfile = current.ActiveProfile;
            OnPropertyChanged(nameof(HasMultipleProfiles));
        }
        finally
        {
            _syncingProfiles = false;
        }
    }

    partial void OnActiveProfileChanged(ServerProfile? value)
    {
        if (_syncingProfiles || value is null || value.Id == _settings.Current.ActiveProfile?.Id) return;
        _ = SwitchProfileAsync(value);
    }

    private async Task SwitchProfileAsync(ServerProfile profile)
    {
        await _settings.SaveAsync(_settings.Current with { ActiveProfileId = profile.Id });
        if (IsSettingsPage) Settings.Load(_settings.Current);
        await _coordinator.SwitchProfileAsync();
    }

    public bool IsSetupMode => Setup is not null;
    public bool IsAvailablePage => CurrentPage == Available;
    public bool IsDownloadedPage => CurrentPage == Downloaded;
    public bool IsDownloadsPage => CurrentPage == Downloads;
    public bool IsSettingsPage => CurrentPage == Settings;

    public bool IsOnline => ConnectionState == ConnectionState.Online;
    public bool IsOffline => ConnectionState is ConnectionState.Offline or ConnectionState.NotConfigured;
    public bool IsConnectionError => ConnectionState == ConnectionState.Error;
    public bool IsConnecting => ConnectionState == ConnectionState.Connecting;

    public string DownloadsBadge => Downloads.ActiveCount > 0 ? Downloads.ActiveCount.ToString() : string.Empty;

    partial void OnConnectionStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsConnectionError));
        OnPropertyChanged(nameof(IsConnecting));
    }

    [RelayCommand]
    private void Navigate(Page page)
    {
        CurrentPage = page switch
        {
            Page.Downloaded => Downloaded,
            Page.Downloads => Downloads,
            Page.Settings => Settings,
            _ => Available,
        };
        if (page == Page.Settings) Settings.Load(_settings.Current);
        if (page == Page.Downloaded) _ = Downloaded.RefreshStorageAsync();
    }

    public async Task StartAsync()
    {
        if (_started || IsSetupMode) return;
        _started = true;
        try
        {
            await _coordinator.InitializeAsync(_settings.Current.AutoRefreshOnStartup);
        }
        catch (Exception ex)
        {
            StartupError = $"Startup problem: {ex.Message}";
        }
        ConfigureRefreshTimer();
    }

    public Task ShutdownAsync() => _downloads.ShutdownAsync();

    private void ConfigureRefreshTimer()
    {
        _refreshTimer?.Stop();
        var minutes = _settings.Current.RefreshIntervalMinutes;
        if (!_started || minutes <= 0) return;
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMinutes(minutes), DispatcherPriority.Background,
            async (_, _) => await _coordinator.RefreshCatalogAsync());
        _refreshTimer.Start();
    }

    private void UpdateStatus()
    {
        ConnectionState = _coordinator.State;
        ConnectionText = _coordinator.State switch
        {
            ConnectionState.Online => _coordinator.StatusMessage ?? "Online",
            ConnectionState.Connecting => "Connecting…",
            ConnectionState.Offline => "Offline",
            ConnectionState.NotConfigured => "Not configured",
            _ => "Connection problem",
        };
        var games = _coordinator.LocalGames;
        StorageText = $"{games.Count} local {(games.Count == 1 ? "game" : "games")} · {SizeFormatter.Format(games.Sum(g => g.ActualSize))}";
    }
}
