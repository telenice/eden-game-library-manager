using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.App.ViewModels;

/// <summary>Snapshot of one transfer, refreshed on a UI timer (worker threads never touch it).</summary>
public sealed partial class DownloadItemViewModel(DownloadTask task) : ViewModelBase
{
    private long _lastBytes = -1;
    private DateTime _lastSample = DateTime.UtcNow;
    private double _speed;

    public DownloadTask Task { get; } = task;
    public string Name => Task.Item.Name;
    public string Subtitle => $"{new GameItemViewModel(Task.Item).TypeText} · {Task.Item.DisplayVersion} · {Task.Item.SourceFileName}";

    [ObservableProperty] private DownloadStatus _status;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _speedText = "-";
    [ObservableProperty] private string _etaText = "-";
    [ObservableProperty] private string? _message;

    public string StatusText => Status.ToString();
    public bool CanPause => Status is DownloadStatus.Downloading or DownloadStatus.Queued;
    public bool CanResume => Status == DownloadStatus.Paused;
    public bool CanRetry => Status == DownloadStatus.Failed;
    public bool CanCancel => !Status.IsTerminal();
    public bool IsActive => Status == DownloadStatus.Downloading;
    public bool IsFailed => Status == DownloadStatus.Failed;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool IsIndeterminate => Status == DownloadStatus.Verifying;

    partial void OnStatusChanged(DownloadStatus value)
    {
        foreach (var name in new[] { nameof(StatusText), nameof(CanPause), nameof(CanResume), nameof(CanRetry),
                     nameof(CanCancel), nameof(IsActive), nameof(IsFailed), nameof(IsIndeterminate) })
            OnPropertyChanged(name);
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    public void Refresh()
    {
        var bytes = Task.DownloadedBytes;
        var total = Math.Max(Task.TotalBytes, 0);
        Status = Task.Status;
        Message = Task.Message;
        Percent = total > 0 ? Math.Min(100, 100.0 * bytes / total) : 0;
        ProgressText = total > 0
            ? $"{SizeFormatter.Format(bytes)} of {SizeFormatter.Format(total)} ({Percent:0.0}%)"
            : SizeFormatter.Format(bytes);

        var now = DateTime.UtcNow;
        if (Status == DownloadStatus.Downloading && _lastBytes >= 0)
        {
            var seconds = (now - _lastSample).TotalSeconds;
            if (seconds > 0)
            {
                var instant = Math.Max(0, bytes - _lastBytes) / seconds;
                _speed = _speed <= 0 ? instant : (_speed * 0.8) + (instant * 0.2);
            }
        }
        else if (Status != DownloadStatus.Downloading)
        {
            _speed = 0;
        }
        _lastBytes = bytes;
        _lastSample = now;

        SpeedText = Status == DownloadStatus.Downloading ? SizeFormatter.FormatSpeed(_speed) : "-";
        EtaText = Status == DownloadStatus.Downloading && _speed > 0 && total > bytes
            ? SizeFormatter.FormatEta(TimeSpan.FromSeconds((total - bytes) / _speed))
            : "-";
    }
}

/// <summary>The download queue screen.</summary>
public sealed partial class DownloadsViewModel : ViewModelBase
{
    private readonly IDownloadManager _downloads;
    private readonly DispatcherTimer _timer;

    public DownloadsViewModel(IDownloadManager downloads)
    {
        _downloads = downloads;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        downloads.TasksChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = [];

    [ObservableProperty] private string _summaryText = "No downloads";
    [ObservableProperty] private int _activeCount;

    public bool IsEmpty => Items.Count == 0;
    public bool HasFinished => Items.Any(i => i.Status is DownloadStatus.Completed or DownloadStatus.Cancelled);

    [RelayCommand]
    private void Pause(DownloadItemViewModel? item)
    {
        if (item is not null) _downloads.Pause(item.Task.Id);
    }

    [RelayCommand]
    private void Resume(DownloadItemViewModel? item)
    {
        if (item is not null) _downloads.Resume(item.Task.Id);
    }

    [RelayCommand]
    private void Retry(DownloadItemViewModel? item)
    {
        if (item is not null) _downloads.Retry(item.Task.Id);
    }

    [RelayCommand]
    private async Task CancelAsync(DownloadItemViewModel? item)
    {
        if (item is not null) await _downloads.CancelAsync(item.Task.Id);
    }

    [RelayCommand]
    private void ClearFinished() => _downloads.ClearFinished();

    private void Refresh()
    {
        var tasks = _downloads.Tasks;
        var ids = tasks.Select(t => t.Id).ToHashSet();
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!ids.Contains(Items[i].Task.Id)) Items.RemoveAt(i);
        foreach (var task in tasks)
            if (Items.All(i => i.Task.Id != task.Id)) Items.Add(new DownloadItemViewModel(task));

        foreach (var item in Items) item.Refresh();

        ActiveCount = Items.Count(i => !i.Status.IsTerminal());
        var downloading = Items.Count(i => i.Status == DownloadStatus.Downloading);
        SummaryText = Items.Count == 0 ? "No downloads" : $"{downloading} downloading · {ActiveCount} unfinished · {Items.Count} total";
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasFinished));
    }
}
