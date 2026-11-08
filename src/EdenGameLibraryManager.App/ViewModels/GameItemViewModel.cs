using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.App.ViewModels;

/// <summary>One catalogue entry on the Available Games screen.</summary>
public sealed partial class GameItemViewModel(RemoteGameItem item) : ViewModelBase
{
    private bool _coverRequested;

    public RemoteGameItem Item { get; private set; } = item;

    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private MatchState _matchState;
    [ObservableProperty] private bool _isQueued;

    public string Name => Item.Name;
    public string SizeText => Item.Size > 0 ? SizeFormatter.Format(Item.Size) : "Unknown size";
    public string TypeText => Item.ContentType switch
    {
        ContentType.Base => "Base game",
        ContentType.Update => "Update",
        ContentType.Dlc => "DLC",
        _ => "Other",
    };
    public string VersionText => Item.DisplayVersion;
    public string FormatText => string.IsNullOrEmpty(Item.FileExtension) ? "?" : Item.FileExtension.ToUpperInvariant();
    public string Subtitle => $"{TypeText} · {VersionText} · {FormatText}";
    public string? TitleId => Item.TitleId;
    public bool HasCover => Cover is not null;

    public bool IsDownloaded => MatchState == MatchState.Downloaded;
    public bool IsPossibleMatch => MatchState == MatchState.PossibleMatch;
    public bool CanDownload => !IsDownloaded && !IsQueued;
    public string DownloadButtonText => IsDownloaded ? "Downloaded" : IsQueued ? "Queued" : "Download";

    partial void OnCoverChanged(Bitmap? value) => OnPropertyChanged(nameof(HasCover));

    partial void OnMatchStateChanged(MatchState value)
    {
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsPossibleMatch));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadButtonText));
    }

    partial void OnIsQueuedChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(DownloadButtonText));
    }

    public string GroupKey => GameGrouping.GroupKey(Item);
    public bool IsBase => Item.ContentType == ContentType.Base;

    public void Update(RemoteGameItem item)
    {
        if (item == Item) return;
        if (item.IconUrl != Item.IconUrl)
        {
            // A different server can reuse the same file id for another game; reload its artwork.
            _coverRequested = false;
            Cover = null;
        }
        Item = item;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Loads artwork once, the first time the card is shown.</summary>
    public async void EnsureCover(IGameMetadataService metadata)
    {
        if (_coverRequested || string.IsNullOrWhiteSpace(Item.IconUrl)) return;
        _coverRequested = true;
        try
        {
            var path = await metadata.GetImageFileAsync(Item.IconUrl!, CancellationToken.None);
            Cover = await ImageLoader.LoadAsync(path, 256);
        }
        catch (Exception)
        {
            // Placeholder artwork stays visible.
        }
    }
}
