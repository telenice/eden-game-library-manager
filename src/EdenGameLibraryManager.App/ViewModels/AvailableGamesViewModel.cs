using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace EdenGameLibraryManager.App.ViewModels;

public enum SortMode
{
    Title,
    SizeLargest,
    SizeSmallest,
    RecentlyAdded,
}

public sealed record SortOption(SortMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The default screen: catalogue items that are not yet in the local library.</summary>
public sealed partial class AvailableGamesViewModel : ViewModelBase
{
    private readonly LibraryCoordinator _coordinator;
    private readonly IGameMetadataService _metadata;
    private readonly IDownloadManager _downloads;
    private readonly ICacheManager _cache;
    private readonly ILocalLibraryService _library;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger<AvailableGamesViewModel> _logger;
    private readonly Dictionary<string, GameItemViewModel> _all = [];

    public AvailableGamesViewModel(
        LibraryCoordinator coordinator,
        IGameMetadataService metadata,
        IDownloadManager downloads,
        ICacheManager cache,
        ILocalLibraryService library,
        ISettingsService settings,
        IDialogService dialogs,
        ILogger<AvailableGamesViewModel> logger)
    {
        _coordinator = coordinator;
        _metadata = metadata;
        _downloads = downloads;
        _cache = cache;
        _library = library;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        _isGridView = settings.Current.ViewMode == ViewMode.Grid;
        _groupByGame = settings.Current.GroupByGame;
        if (_groupByGame)
        {
            _showUpdates = true;
            _showDlc = true;
        }
        _selectedSort = SortOptions[0];

        coordinator.Changed += (_, _) => Sync();
        downloads.TasksChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            UpdateQueuedFlags();
            if (GroupByGame) ApplyFilter();
        });
    }

    public ObservableCollection<GameItemViewModel> Items { get; } = [];
    public ObservableCollection<GameGroupViewModel> Groups { get; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(SortMode.Title, "Title (A-Z)"),
        new(SortMode.SizeLargest, "Size (largest)"),
        new(SortMode.SizeSmallest, "Size (smallest)"),
        new(SortMode.RecentlyAdded, "Recently added"),
    ];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private SortOption _selectedSort;
    [ObservableProperty] private bool _showBase = true;
    [ObservableProperty] private bool _showUpdates;
    [ObservableProperty] private bool _showDlc;
    [ObservableProperty] private bool _showDownloaded;
    [ObservableProperty] private bool _isGridView;
    [ObservableProperty] private bool _groupByGame;
    [ObservableProperty] private GameItemViewModel? _selectedGame;
    [ObservableProperty] private GameDetailsViewModel? _details;
    [ObservableProperty] private string _summaryText = string.Empty;

    public bool IsListView => !IsGridView;
    public bool ShowItemGrid => IsGridView && !GroupByGame;
    public bool ShowItemList => !IsGridView && !GroupByGame;
    public bool ShowGroupGrid => IsGridView && GroupByGame;
    public bool ShowGroupList => !IsGridView && GroupByGame;
    public bool IsRefreshing => _coordinator.IsRefreshing;
    public bool HasDetails => Details is not null;
    public bool IsEmpty => GroupByGame ? Groups.Count == 0 : Items.Count == 0;

    public string EmptyText => _coordinator.Catalog is null
        ? (_coordinator.State == ConnectionState.NotConfigured
            ? "Connect to your AeroFoil server in Settings to see available games."
            : _coordinator.StatusMessage ?? "No catalogue loaded yet.")
        : "No games match the current filters.";

    public string? WarningText
    {
        get
        {
            var catalog = _coordinator.Catalog;
            if (catalog is null) return _coordinator.State == ConnectionState.Error ? _coordinator.StatusMessage : null;
            var warnings = new List<string>();
            if (_coordinator.State is ConnectionState.Error or ConnectionState.Offline && _coordinator.StatusMessage is { } msg)
                warnings.Add(msg);
            if (catalog.FromCache && _coordinator.State != ConnectionState.Offline)
                warnings.Add($"Showing the catalogue saved {catalog.RetrievedAt.LocalDateTime:g}; it may be out of date.");
            warnings.AddRange(catalog.Warnings.Where(w => !warnings.Contains(w)));
            return warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings.Distinct());
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(WarningText);

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnSelectedSortChanged(SortOption value) => ApplyFilter();
    partial void OnShowBaseChanged(bool value) => ApplyFilter();
    partial void OnShowUpdatesChanged(bool value) => ApplyFilter();
    partial void OnShowDlcChanged(bool value) => ApplyFilter();
    partial void OnShowDownloadedChanged(bool value) => ApplyFilter();
    partial void OnDetailsChanged(GameDetailsViewModel? value) => OnPropertyChanged(nameof(HasDetails));

    partial void OnIsGridViewChanged(bool value)
    {
        RaiseLayoutFlags();
        _ = _settings.SaveAsync(_settings.Current with { ViewMode = value ? ViewMode.Grid : ViewMode.List });
    }

    partial void OnGroupByGameChanged(bool value)
    {
        if (value)
        {
            // Grouping is mostly useful for spotting new updates and DLC for games you have.
            ShowUpdates = true;
            ShowDlc = true;
        }
        RaiseLayoutFlags();
        ApplyFilter();
        _ = _settings.SaveAsync(_settings.Current with { GroupByGame = value });
    }

    partial void OnSelectedGameChanged(GameItemViewModel? value)
    {
        if (value is null)
        {
            Details = null;
            return;
        }
        var match = _coordinator.Matches.TryGetValue(value.Item.Key, out var m) ? m : MatchResult.None;
        var related = OrderMembers(_all.Values.Where(v => v.GroupKey == value.GroupKey));
        Details = new GameDetailsViewModel(value, match, related);
        _ = Details.LoadAsync(_metadata);
    }

    [RelayCommand]
    private Task RefreshAsync() => _coordinator.RefreshCatalogAsync();

    [RelayCommand]
    private void SelectGame(GameItemViewModel? game) => SelectedGame = game;

    [RelayCommand]
    private void SelectGroup(GameGroupViewModel? group) => SelectedGame = group?.Primary;

    [RelayCommand]
    private void CloseDetails() => SelectedGame = null;

    [RelayCommand]
    private void SetGridView() => IsGridView = true;

    [RelayCommand]
    private void SetListView() => IsGridView = false;

    /// <summary>
    /// Downloads one item. For a base game (when enabled in Settings) its latest update and DLC from
    /// the same server are queued too; the server lists only the newest update for each title.
    /// </summary>
    [RelayCommand]
    private async Task DownloadAsync(GameItemViewModel? game)
    {
        if (game is null || !game.CanDownload) return;
        var items = new List<GameItemViewModel> { game };
        if (game.IsBase && _settings.Current.IncludeUpdatesAndDlc)
            items.AddRange(OrderMembers(_all.Values.Where(v => v.GroupKey == game.GroupKey && !v.IsBase && v.CanDownload)));

        var label = items.Count > 1 ? $"{game.Name} (with {items.Count - 1} update/DLC item{(items.Count > 2 ? "s" : "")})" : game.Name;
        if (await PrepareAsync(label, items.Sum(i => i.Item.Size)))
            await EnqueueAsync(items);
    }

    /// <summary>
    /// Makes updates/DLC carry the base game's name so they share its per-game folder. The base entry
    /// in the catalogue wins over the item's own <c>title_name</c>, which is only a fallback.
    /// </summary>
    private RemoteGameItem WithTitleName(GameItemViewModel game)
    {
        var baseName = _all.Values.FirstOrDefault(v => v.IsBase && v.GroupKey == game.GroupKey)?.Name;
        return baseName is null || baseName == game.Item.TitleName ? game.Item : game.Item with { TitleName = baseName };
    }

    /// <summary>Downloads every item of the group that passes the filters and isn't local yet.</summary>
    [RelayCommand]
    private async Task DownloadGroupAsync(GameGroupViewModel? group)
    {
        if (group is null || group.Pending.Count == 0) return;
        var items = group.Pending.Where(p => p.CanDownload).ToList();
        if (items.Count == 0) return;
        if (await PrepareAsync(group.Name, items.Sum(i => i.Item.Size)))
            await EnqueueAsync(items);
    }

    /// <summary>Checks connectivity, the games directory and the cache limit (offering cleanup) before queueing.</summary>
    private async Task<bool> PrepareAsync(string name, long totalBytes)
    {
        if (!_coordinator.CanDownload)
        {
            await _dialogs.ShowMessageAsync("Server unavailable",
                "New downloads are disabled while the AeroFoil server is offline or unreachable. Refresh once the connection is back.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_settings.Current.GamesDirectory))
        {
            await _dialogs.ShowMessageAsync("Games directory", "Choose a local games directory in Settings first.");
            return false;
        }

        var plan = _cache.Plan(totalBytes, _downloads.GetReservedBytes());
        if (plan.Fits) return true;

        if (_settings.Current.AutoCleanup)
        {
            if (plan.CanSatisfyByEviction) return true; // least recently used games are removed when the transfer starts
            await _dialogs.ShowMessageAsync("Not enough cache space",
                $"Even after removing every unpinned downloaded game, {SizeFormatter.Format(plan.Shortfall)} more space is needed. " +
                "Increase the cache limit or unpin some games.");
            return false;
        }
        return await RunManualCleanupAsync(name, totalBytes, plan);
    }

    private async Task EnqueueAsync(IEnumerable<GameItemViewModel> games)
    {
        foreach (var game in games)
        {
            try
            {
                _downloads.Enqueue(WithTitleName(game));
                game.IsQueued = true;
            }
            catch (Exception ex) when (ex is AeroFoilException or UnsafePathException)
            {
                _logger.LogWarning("Could not queue {Name}: {Error}", game.Name, ex.Message);
                await _dialogs.ShowMessageAsync("Cannot download", $"{game.Name}: {ex.Message}");
            }
        }
        if (GroupByGame) ApplyFilter();
    }

    private async Task<bool> RunManualCleanupAsync(string name, long totalBytes, CachePlan plan)
    {
        var preselected = plan.Evictions.Select(e => e.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = _library.Games
            .Where(g => g.IsManaged)
            .OrderBy(g => g.IsPinned)
            .ThenBy(g => g.LastUsed)
            .Select(g => new CleanupCandidateViewModel(g, _coordinator.FindRemoteFor(g)?.Name ?? g.DisplayName, preselected.Contains(g.LocalPath)))
            .ToList();

        if (candidates.Count == 0)
        {
            await _dialogs.ShowMessageAsync("Cache limit reached",
                $"This download needs {SizeFormatter.Format(plan.BytesToFree)} more than your cache limit allows. Increase the limit in Settings.");
            return false;
        }

        var deleted = await _dialogs.ShowCleanupAsync(new CleanupDialogViewModel(name, plan, candidates, _library));
        if (deleted) _coordinator.Recompute();
        return deleted && _cache.Plan(totalBytes, _downloads.GetReservedBytes()).Fits;
    }

    private void Sync()
    {
        var catalog = _coordinator.Catalog;
        var keys = new HashSet<string>();
        if (catalog is not null)
        {
            foreach (var item in catalog.Items)
            {
                keys.Add(item.Key);
                if (_all.TryGetValue(item.Key, out var vm)) vm.Update(item);
                else _all[item.Key] = new GameItemViewModel(item);
            }
        }
        foreach (var stale in _all.Keys.Where(k => !keys.Contains(k)).ToList()) _all.Remove(stale);

        foreach (var vm in _all.Values)
            vm.MatchState = _coordinator.Matches.TryGetValue(vm.Item.Key, out var m) ? m.State : MatchState.NotDownloaded;

        UpdateQueuedFlags();
        ApplyFilter();
        OnPropertyChanged(nameof(IsRefreshing));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void UpdateQueuedFlags()
    {
        foreach (var vm in _all.Values) vm.IsQueued = _downloads.IsQueuedOrActive(vm.Item.Key);
    }

    private bool PassesFilter(GameItemViewModel vm) =>
        (vm.Item.ContentType switch
        {
            ContentType.Update => ShowUpdates,
            ContentType.Dlc => ShowDlc,
            _ => ShowBase,
        }) &&
        (ShowDownloaded || !vm.IsDownloaded);

    private bool MatchesSearch(GameItemViewModel vm, string search) =>
        search.Length == 0 ||
        vm.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        (vm.Item.TitleName?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
        (vm.TitleId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        if (GroupByGame) ApplyGroupedFilter(search);
        else ApplyItemFilter(search);

        var hidden = _all.Values.Count(v => v.IsDownloaded);
        SummaryText = GroupByGame
            ? $"{Groups.Count} games shown · {hidden} items already downloaded"
            : $"{Items.Count} shown · {hidden} already downloaded";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void ApplyItemFilter(string search)
    {
        IEnumerable<GameItemViewModel> query = _all.Values.Where(vm => PassesFilter(vm) && MatchesSearch(vm, search));
        query = SelectedSort.Mode switch
        {
            SortMode.SizeLargest => query.OrderByDescending(v => v.Item.Size),
            SortMode.SizeSmallest => query.OrderBy(v => v.Item.Size),
            // AeroFoil file ids increase as files are added (its own "New" section sorts by them).
            SortMode.RecentlyAdded => query.OrderByDescending(v => v.Item.FileId ?? 0).ThenByDescending(v => v.Item.ReleaseDate),
            _ => query.OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var list = query.ToList();
        Groups.Clear();
        Items.Clear();
        foreach (var vm in list) Items.Add(vm);
        foreach (var vm in list) vm.EnsureCover(_metadata);
    }

    private void ApplyGroupedFilter(string search)
    {
        var groups = new List<GameGroupViewModel>();
        foreach (var members in _all.Values.GroupBy(v => v.GroupKey))
        {
            var ordered = OrderMembers(members);
            var visible = ordered.Where(PassesFilter).ToList();
            if (visible.Count == 0 || !ordered.Any(m => MatchesSearch(m, search))) continue;
            var pending = visible.Where(v => !v.IsDownloaded && !v.IsQueued).ToList();
            groups.Add(new GameGroupViewModel(members.Key, ordered, pending));
        }

        IEnumerable<GameGroupViewModel> query = SelectedSort.Mode switch
        {
            SortMode.SizeLargest => groups.OrderByDescending(g => g.PendingBytes),
            SortMode.SizeSmallest => groups.OrderBy(g => g.PendingBytes),
            SortMode.RecentlyAdded => groups.OrderByDescending(g => g.MaxFileId),
            _ => groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        Items.Clear();
        Groups.Clear();
        foreach (var group in query)
        {
            Groups.Add(group);
            group.Primary.EnsureCover(_metadata);
        }
    }

    private static List<GameItemViewModel> OrderMembers(IEnumerable<GameItemViewModel> members)
    {
        var byItem = members.ToDictionary(m => m.Item);
        return GameGrouping.OrderMembers(byItem.Keys).Select(i => byItem[i]).ToList();
    }

    private void RaiseLayoutFlags()
    {
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(ShowItemGrid));
        OnPropertyChanged(nameof(ShowItemList));
        OnPropertyChanged(nameof(ShowGroupGrid));
        OnPropertyChanged(nameof(ShowGroupList));
    }
}
