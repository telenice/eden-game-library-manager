using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;

namespace EdenGameLibraryManager.App.ViewModels;

/// <summary>First-run wizard: connect, choose the games directory, configure storage, finish.</summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly ICredentialStore _credentials;
    private readonly IDialogService _dialogs;

    public SetupViewModel(ISettingsService settings, ICredentialStore credentials, IGameLibraryProvider provider, IDialogService dialogs)
    {
        _settings = settings;
        _credentials = credentials;
        _dialogs = dialogs;
        Connection = new ConnectionFormViewModel(provider, credentials,
            settings.Current.ActiveProfile ?? new ServerProfile { Id = ServerProfile.NewId() });
        _gamesDirectory = settings.Current.GamesDirectory;
        _cacheLimitGb = (decimal)settings.Current.CacheLimitGb;
    }

    public ConnectionFormViewModel Connection { get; }

    public event EventHandler? Completed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsStep4), nameof(CanGoBack), nameof(NextText), nameof(StepTitle))]
    private int _step = 1;

    [ObservableProperty] private string _gamesDirectory;
    [ObservableProperty] private decimal _cacheLimitGb;
    [ObservableProperty] private bool _autoCleanup;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsStep4 => Step == 4;
    public bool CanGoBack => Step > 1;
    public string NextText => Step == 4 ? "Finish" : "Next";

    public string StepTitle => Step switch
    {
        1 => "Step 1 of 4 · Connect to AeroFoil or TorBox",
        2 => "Step 2 of 4 · Choose games directory",
        3 => "Step 3 of 4 · Configure storage",
        _ => "Step 4 of 4 · Finish",
    };

    public string CacheSummary => CacheLimitGb <= 0 ? "Unlimited" : $"{CacheLimitGb:0.#} GB";

    partial void OnCacheLimitGbChanged(decimal value) => OnPropertyChanged(nameof(CacheSummary));

    [RelayCommand]
    private void Back()
    {
        Error = null;
        if (Step > 1) Step--;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var folder = await _dialogs.PickFolderAsync("Choose where games are downloaded", GamesDirectory);
        if (folder is not null) GamesDirectory = folder;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            switch (Step)
            {
                case 1:
                    if (Connection.TestSucceeded != true && !await Connection.TestAsync())
                    {
                        Error = "The connection test must succeed before continuing.";
                        return;
                    }
                    Step = 2;
                    break;

                case 2:
                    if (string.IsNullOrWhiteSpace(GamesDirectory))
                    {
                        Error = "Choose a folder for your games.";
                        return;
                    }
                    try
                    {
                        Directory.CreateDirectory(GamesDirectory);
                    }
                    catch (Exception ex)
                    {
                        Error = $"This folder cannot be used: {ex.Message}";
                        return;
                    }
                    Step = 3;
                    break;

                case 3:
                    Step = 4;
                    break;

                default:
                    await FinishAsync();
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task FinishAsync()
    {
        var connection = Connection.TryBuildConnection();
        if (connection is null)
        {
            Step = 1;
            return;
        }

        _credentials.SetPassword(Connection.Id, Connection.Password, Connection.RememberPassword);
        var profile = Connection.ToProfile(connection.BaseUri.ToString());
        var current = _settings.Current;
        await _settings.SaveAsync(current with
        {
            Profiles = [.. current.Profiles.Where(p => p.Id != profile.Id), profile],
            ActiveProfileId = profile.Id,
            GamesDirectory = GamesDirectory.Trim(),
            CacheLimitGb = (double)CacheLimitGb,
            AutoCleanup = AutoCleanup,
            SetupCompleted = true,
        });
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
