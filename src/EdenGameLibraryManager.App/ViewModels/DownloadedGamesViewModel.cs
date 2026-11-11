using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.App.ViewModels;

public sealed partial class LocalGameViewModel : ViewModelBase
{
    private bool _coverRequested;

    public LocalGameViewModel(LocalGame game, RemoteGameItem? remote)
    {
        Game = game;
        Remote = remote;
    }

    public LocalGame Game { get; private set; }
    public RemoteGameItem? Remote { get; private set; }

    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private bool _isSelected;

    /// <summary>True for an update or DLC listed under its base game.</summary>
    [ObservableProperty] private bool _isChild;
    [ObservableProperty] private string? _parentText;

    public string GroupKey => GameGrouping.GroupKey(Game);

    public string Name => !string.IsNullOrWhiteSpace(Game.Name) ? Game.Name! : Remote?.Name ?? Game.DisplayName;
    public string SizeText => SizeFormatter.Format(Game.ActualSize);
    public string PathText => Game.LocalPath;
    public string TypeText => Game.ContentType switch
    {
        ContentType.Base => "Base game",
        ContentType.Update => "Update",
        ContentType.Dlc => "DLC",
        _ => "Unknown type",
    };
    public string VersionText => Game.Version is { } v ? $"v{v}" : "version unknown";
    public string OriginText => Game.IsManaged ? "Downloaded by this app" : "Added manually";
    public string DownloadedText => Game.DownloadedAt is { } d ? d.LocalDateTime.ToString("g") : "-";
    /// <summary>
    /// Eden's total play time for the game, shown on base game rows only (updates and DLC share it).
    /// Empty when Eden's play-time file wasn't found.
    /// </summary>
    public string PlayTimeText => IsChild || PlayTime is not { } t ? string.Empty : $"   Total playtime: {FormatPlayTime(t)}";

    /// <summary>Null when Eden's play-time file wasn't found; zero when the game hasn't been played.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayTimeText))]
    private TimeSpan? _playTime;

    partial void OnIsChildChanged(bool value) => OnPropertyChanged(nameof(PlayTimeText));

    public static string FormatPlayTime(TimeSpan t) => t.TotalSeconds switch
    {
        < 1 => "Not played yet",
        < 60 => "Less than a minute",
        < 3600 => $"{(int)t.TotalMinutes} min",
        _ => t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {t.Minutes} min",
    };
    public bool IsPinned => Game.IsPinned;
    public bool IsUnverified => Game.Status == LocalGameStatus.Unverified;
    public string PinButtonText => IsPinned ? "Unpin" : "Pin";
    public string Details => $"{TypeText} · {VersionText} · {OriginText}";

    public void Update(LocalGame game, RemoteGameItem? remote)
    {
        if (game == Game && remote == Remote) return;
        Game = game;
        Remote = remote;
        OnPropertyChanged(string.Empty);
    }

    public async void EnsureCover(IGameMetadataService metadata)
    {
        if (_coverRequested || string.IsNullOrWhiteSpace(Remote?.IconUrl)) return;
        _coverRequested = true;
        try
        {
            var path = await metadata.GetImageFileAsync(Remote!.IconUrl!, CancellationToken.None);
            Cover = await ImageLoader.LoadAsync(path, 128);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>Games present in the local games directory, with storage usage and deletion.</summary>
public sealed partial class DownloadedGamesViewModel : ViewModelBase
{
    private readonly LibraryCoordinator _coordinator;
    private readonly ILocalLibraryService _library;
    private readonly ICacheManager _cache;
    private readonly IDownloadManager _downloads;
    private readonly IGameMetadataService _metadata;
    private readonly IPlayTimeService _playTime;
    private readonly IDialogService _dialogs;
    private readonly ILogger<DownloadedGamesViewModel> _logger;
    private readonly Dictionary<string, LocalGameViewModel> _all = new(StringComparer.OrdinalIgnoreCase);

    public DownloadedGamesViewModel(
        LibraryCoordinator coordinator,
        ILocalLibraryService library,
        ICacheManager cache,
        IDownloadManager downloads,
        IGameMetadataService metadata,
        IPlayTimeService playTime,
        IDialogService dialogs,
        ILogger<DownloadedGamesViewModel> logger)
    {
        _coordinator = coordinator;
        _library = library;
        _cache = cache;
        _downloads = downloads;
        _metadata = metadata;
        _playTime = playTime;
        _dialogs = dialogs;
        _logger = logger;
        coordinator.Changed += (_, _) => Sync();
    }

    public ObservableCollection<LocalGameViewModel> Items { get; } = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private StorageInfo? _storage;

    public bool IsEmpty => Items.Count == 0;
    public long SelectedBytes => _all.Values.Where(v => v.IsSelected).Sum(v => v.Game.ActualSize);
    public bool HasSelection => _all.Values.Any(v => v.IsSelected);
    public string DeleteSelectedText => $"Delete selected ({SizeFormatter.Format(SelectedBytes)})";

    public string LimitText => Storage is null ? "-" : Storage.CacheLimitBytes <= 0 ? "Unlimited" : SizeFormatter.Format(Storage.CacheLimitBytes);
    public string UsedText => Storage is null ? "-" : SizeFormatter.Format(Storage.UsedBytes) +
        (Storage.ReservedBytes > 0 ? $" (incl. {SizeFormatter.Format(Storage.ReservedBytes)} reserved for downloads)" : string.Empty);
    public string FreeUnderLimitText => Storage?.FreeUnderLimitBytes is { } f ? SizeFormatter.Format(f) : "-";
    public string DiskFreeText => Storage?.DiskFreeBytes is { } f ? SizeFormatter.Format(f) : "Unknown";
    public string ManualText => Storage is null ? "-" : SizeFormatter.Format(Storage.UnmanagedBytes);
    public string CountText => Storage is null ? "-" : $"{Storage.GameCount} games · {Storage.PinnedCount} pinned";
    public double UsagePercent => Storage is null || Storage.CacheLimitBytes <= 0 ? 0 : Math.Min(100, 100.0 * Storage.UsedBytes / Storage.CacheLimitBytes);
    public bool HasLimit => Storage?.CacheLimitBytes > 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnStorageChanged(StorageInfo? value)
    {
        foreach (var name in new[] { nameof(LimitText), nameof(UsedText), nameof(FreeUnderLimitText), nameof(DiskFreeText),
                     nameof(ManualText), nameof(CountText), nameof(UsagePercent), nameof(HasLimit) })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private Task RefreshAsync() => _coordinator.RescanAsync();

    [RelayCommand]
    private async Task OpenFolderAsync(LocalGameViewModel? game)
    {
        if (game is null) return;
        try
        {
            PlatformShell.RevealInFileManager(game.Game.LocalPath);
            await _library.MarkAccessedAsync(game.Game, CancellationToken.None);
            _coordinator.Recompute();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Open folder", $"Could not open the folder: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task TogglePinAsync(LocalGameViewModel? game)
    {
        if (game is null) return;
        await _library.SetPinnedAsync(game.Game, !game.IsPinned, CancellationToken.None);
        _coordinator.Recompute();
    }

    [RelayCommand]
    private async Task DeleteAsync(LocalGameViewModel? game)
    {
        if (game is null) return;
        var pinnedNote = game.IsPinned ? " This game is pinned." : string.Empty;
        if (!await _dialogs.ConfirmAsync("Delete game",
                $"Delete \"{game.Name}\" ({game.SizeText}) from your computer?{pinnedNote}\n\n{game.PathText}\n\n" +
                "Only this game file is removed. Emulator saves, settings, firmware and keys are not touched.",
                "Delete", destructive: true))
            return;
        await DeleteGamesAsync([game]);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var selected = _all.Values.Where(v => v.IsSelected).ToList();
        if (selected.Count == 0) return;
        var pinned = selected.Count(s => s.IsPinned);
        if (!await _dialogs.ConfirmAsync("Delete games",
                $"Delete {selected.Count} game file(s) and recover {SizeFormatter.Format(SelectedBytes)}?" +
                (pinned > 0 ? $" {pinned} of them are pinned." : string.Empty) +
                "\n\nEmulator saves, settings, firmware and keys are not touched.",
                "Delete", destructive: true))
            return;
        await DeleteGamesAsync(selected);
    }

    private async Task DeleteGamesAsync(IEnumerable<LocalGameViewModel> games)
    {
        var errors = new List<string>();
        foreach (var game in games)
        {
            try
            {
                await Task.Run(() => _library.DeleteAsync(game.Game, CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Delete failed for {Path}", game.Game.LocalPath);
                errors.Add($"{game.Name}: {ex.Message}");
            }
        }
        _coordinator.Recompute();
        if (errors.Count > 0)
            await _dialogs.ShowMessageAsync("Some files were not deleted", string.Join(Environment.NewLine, errors));
    }

    /// <summary>Refreshes the storage summary and Eden's play times (called when the page is shown).</summary>
    public async Task RefreshStorageAsync()
    {
        try
        {
            Storage = await _cache.GetStorageInfoAsync(_downloads.GetReservedBytes(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Storage info unavailable");
        }
        await RefreshPlayTimesAsync();
    }

    private async Task RefreshPlayTimesAsync()
    {
        IReadOnlyDictionary<string, TimeSpan>? times;
        try
        {
            times = await _playTime.GetPlayTimesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Eden play time unavailable");
            return;
        }
        foreach (var vm in _all.Values)
            vm.PlayTime = times is null ? null : times.TryGetValue(vm.GroupKey, out var t) ? t : TimeSpan.Zero;
    }

    private void Sync()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in _coordinator.LocalGames)
        {
            seen.Add(game.LocalPath);
            var remote = _coordinator.FindRemoteFor(game);
            if (_all.TryGetValue(game.LocalPath, out var vm)) vm.Update(game, remote);
            else
            {
                vm = new LocalGameViewModel(game, remote);
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(LocalGameViewModel.IsSelected)) return;
                    OnPropertyChanged(nameof(SelectedBytes));
                    OnPropertyChanged(nameof(HasSelection));
                    OnPropertyChanged(nameof(DeleteSelectedText));
                };
                _all[game.LocalPath] = vm;
            }
        }
        foreach (var stale in _all.Keys.Where(k => !seen.Contains(k)).ToList()) _all.Remove(stale);

        ApplyFilter();
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DeleteSelectedText));
        _ = RefreshStorageAsync();
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var matches = _all.Values
            .Where(v => search.Length == 0 ||
                        v.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                        v.PathText.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();

        // Updates and DLC are listed directly under their base game when it is present.
        var bases = _all.Values
            .Where(v => v.Game.ContentType == ContentType.Base)
            .GroupBy(v => v.GroupKey)
            .ToDictionary(g => g.Key, g => g.First());

        string GroupName(LocalGameViewModel v) =>
            bases.TryGetValue(v.GroupKey, out var b) ? b.Name : v.Remote?.TitleName ?? v.Name;

        var list = matches
            .OrderBy(GroupName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.GroupKey, StringComparer.Ordinal)
            .ThenBy(v => GameGrouping.ContentOrder(v.Game.ContentType))
            .ThenByDescending(v => v.Game.Version ?? 0)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var vm in list)
        {
            var hasBase = vm.Game.ContentType != ContentType.Base && bases.ContainsKey(vm.GroupKey);
            vm.IsChild = hasBase;
            vm.ParentText = hasBase ? $"{vm.TypeText} for {GroupName(vm)}" : null;
            vm.EnsureCover(_metadata);
        }

        // The library raises Changed often (rescans, catalogue refreshes, finished downloads); only
        // rebuild the list when its contents or order actually changed, since rebuilding recreates every row.
        if (!Items.SequenceEqual(list))
        {
            Items.Clear();
            foreach (var vm in list) Items.Add(vm);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }
}
