using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.App.ViewModels;

public sealed partial class CleanupCandidateViewModel : ViewModelBase
{
    public CleanupCandidateViewModel(LocalGame game, string name, bool selected)
    {
        Game = game;
        Name = name;
        _isSelected = selected;
    }

    public LocalGame Game { get; }
    public string Name { get; }
    public string SizeText => SizeFormatter.Format(Game.ActualSize);
    public string LastUsedText => $"Last used {Game.LastUsed.LocalDateTime:d}";
    public bool IsPinned => Game.IsPinned;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Manual cache cleanup: lets the user choose downloaded games to remove when a new download would
/// exceed the cache limit. Least-recently-used, unpinned games are preselected.
/// </summary>
public sealed partial class CleanupDialogViewModel : ViewModelBase
{
    private readonly ILocalLibraryService _library;

    public CleanupDialogViewModel(string gameName, CachePlan plan, IEnumerable<CleanupCandidateViewModel> candidates, ILocalLibraryService library)
    {
        _library = library;
        GameName = gameName;
        Plan = plan;
        foreach (var c in candidates)
        {
            c.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CleanupCandidateViewModel.IsSelected)) Refresh();
            };
            Candidates.Add(c);
        }
    }

    public string GameName { get; }
    public CachePlan Plan { get; }
    public ObservableCollection<CleanupCandidateViewModel> Candidates { get; } = [];

    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty] private string? _error;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isBusy;

    public string Intro =>
        $"Downloading \"{GameName}\" needs {SizeFormatter.Format(Plan.RequiredBytes)}, which exceeds your cache limit by " +
        $"{SizeFormatter.Format(Plan.BytesToFree)}. Select downloaded games to remove, or cancel and raise the limit in Settings.";

    public long SelectedBytes => Candidates.Where(c => c.IsSelected).Sum(c => c.Game.ActualSize);
    public string SelectionText => $"Selected: {SizeFormatter.Format(SelectedBytes)} of {SizeFormatter.Format(Plan.BytesToFree)} needed";
    public bool IsEnough => SelectedBytes >= Plan.BytesToFree;
    public string DeleteButtonText => $"Delete selected ({SizeFormatter.Format(SelectedBytes)})";
    public bool HasCandidates => Candidates.Count > 0;

    private void Refresh()
    {
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(IsEnough));
        OnPropertyChanged(nameof(DeleteButtonText));
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private bool CanDelete() => !IsBusy && IsEnough;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            foreach (var c in Candidates.Where(c => c.IsSelected).ToList())
                await Task.Run(() => _library.DeleteAsync(c.Game, CancellationToken.None));
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
