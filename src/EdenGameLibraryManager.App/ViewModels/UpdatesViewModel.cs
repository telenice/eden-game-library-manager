using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.App.ViewModels;

/// <summary>
/// App update state, shared by the banner in the main window and the Updates section in Settings.
/// Installing swaps the executable and then asks the app to close and start the new version.
/// </summary>
public sealed partial class UpdatesViewModel : ViewModelBase
{
    private readonly IUpdateService _updates;
    private readonly IDownloadManager _downloads;
    private readonly IDialogService _dialogs;
    private readonly ILogger<UpdatesViewModel> _logger;

    public UpdatesViewModel(IUpdateService updates, IDownloadManager downloads, IDialogService dialogs, ILogger<UpdatesViewModel> logger)
    {
        _updates = updates;
        _downloads = downloads;
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>Raised on the UI thread with the program to start once the app has closed.</summary>
    public event EventHandler<UpdateRestart>? RestartRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner), nameof(BannerText), nameof(CanInstall), nameof(HasUpdate), nameof(ReleaseButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(OpenReleasePageCommand))]
    private AppUpdate? _available;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    private bool _isDismissed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand))]
    private bool _isChecking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CheckNowCommand))]
    private bool _isInstalling;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string? _statusText;

    public string CurrentVersionText => $"You have version {_updates.CurrentVersion}.";
    public bool HasUpdate => Available is not null;
    public bool ShowBanner => Available is not null && !IsDismissed;
    public bool CanInstall => Available?.CanInstallAutomatically == true && _updates.CanInstallInPlace && !IsInstalling;
    public string BannerText => Available is null ? string.Empty : $"Version {Available.Version} is available (you have {_updates.CurrentVersion}).";

    /// <summary>"What's new" when the app can install the update itself, otherwise "Download" (macOS, Linux, development builds).</summary>
    public string ReleaseButtonText => CanInstall ? "What's new" : "Download";

    /// <summary>Removes what a finished update left behind (the previous .exe and the staged copy).</summary>
    public async Task CleanUpAsync()
    {
        try
        {
            await _updates.CleanUpAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Update clean-up skipped: {Error}", ex.Message);
        }
    }

    /// <summary>Quiet check at startup: failures are only logged.</summary>
    public async Task CheckOnStartupAsync()
    {
        try
        {
            await CheckCoreAsync(quiet: true);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Update check skipped: {Error}", ex.Message);
        }
    }

    private bool CanCheck() => !IsChecking && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckCoreAsync(quiet: false);

    private async Task CheckCoreAsync(bool quiet)
    {
        IsChecking = true;
        if (!quiet) StatusText = "Checking for updates…";
        try
        {
            var update = await Task.Run(() => _updates.CheckAsync(CancellationToken.None));
            Available = update;
            IsDismissed = false;
            if (update is null)
                StatusText = quiet ? null : $"You're on the latest version ({_updates.CurrentVersion}).";
            else
            {
                StatusText = !update.CanInstallAutomatically || !_updates.CanInstallInPlace
                    ? "This copy can't update itself; download the new version from the release page."
                    : null;
                _logger.LogInformation("Update available: {Version}", update.Version);
            }
        }
        catch (UpdateException ex)
        {
            _logger.LogInformation("Update check failed: {Error}", ex.Message);
            if (!quiet) StatusText = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    private bool CanRunInstall() => CanInstall;

    [RelayCommand(CanExecute = nameof(CanRunInstall))]
    private async Task InstallAsync()
    {
        var update = Available;
        if (update is null) return;

        var active = _downloads.Tasks.Count(t => t.Status is DownloadStatus.Downloading or DownloadStatus.Queued);
        var message = $"Download version {update.Version} ({Core.Services.SizeFormatter.Format(update.PackageSize)}), then restart the app?";
        if (active > 0) message += $"\n\n{active} download(s) in progress will pause and resume after the restart.";
        if (!await _dialogs.ConfirmAsync("Update the app", message, "Update and restart")) return;

        IsInstalling = true;
        ProgressPercent = 0;
        StatusText = "Downloading the update…";
        try
        {
            var progress = new Progress<double>(p => ProgressPercent = p * 100);
            var restart = await Task.Run(() => _updates.InstallAsync(update, progress, CancellationToken.None));
            StatusText = "Restarting…";
            RestartRequested?.Invoke(this, restart);
        }
        catch (UpdateException ex)
        {
            _logger.LogWarning("Update failed: {Error}", ex.Message);
            StatusText = ex.Message;
            await _dialogs.ShowMessageAsync("Update failed", ex.Message + "\n\nNothing was changed; this version keeps running.");
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private bool HasRelease() => Available is not null;

    [RelayCommand(CanExecute = nameof(HasRelease))]
    private void OpenReleasePage()
    {
        if (Available is { } update) PlatformShell.OpenWebPage(update.ReleasePage);
    }

    [RelayCommand]
    private void Dismiss() => IsDismissed = true;
}
