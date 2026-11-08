using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using EdenGameLibraryManager.App.Services;
using EdenGameLibraryManager.Core.Interfaces;
using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.App.ViewModels;

/// <summary>Side panel with metadata, description and screenshots for the selected game.</summary>
public sealed partial class GameDetailsViewModel : ViewModelBase
{
    public GameDetailsViewModel(GameItemViewModel game, MatchResult match, IReadOnlyList<GameItemViewModel> related)
    {
        Game = game;
        Match = match;
        Related = related;
    }

    public GameItemViewModel Game { get; }
    public MatchResult Match { get; }

    /// <summary>Base game, updates and DLC for the same title (including <see cref="Game"/>).</summary>
    public IReadOnlyList<GameItemViewModel> Related { get; }

    public bool HasRelated => Related.Count > 1;
    public RemoteGameItem Item => Game.Item;

    public ObservableCollection<Bitmap> Screenshots { get; } = [];

    [ObservableProperty] private string? _description;
    [ObservableProperty] private bool _isLoading;

    public string TitleIdText => Item.TitleId ?? "Unknown";
    public string AppIdText => Item.EffectiveAppId ?? "Unknown";
    public string ReleaseDateText => Item.ReleaseDate?.ToString("d") ?? "Unknown";
    public string CategoryText => Item.Category ?? "-";
    public string FileNameText => Item.SourceFileName ?? "-";
    public bool HasScreenshots => Screenshots.Count > 0;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string LocalStatusText => Match.State switch
    {
        MatchState.Downloaded => $"Already in your library: {Match.Local?.FileName}",
        MatchState.PossibleMatch => $"A related local file was found but could not be verified: {Match.Local?.FileName}",
        _ => "Not downloaded",
    };

    public string FormatNote => Item.FileExtension switch
    {
        "nsz" or "xcz" => "Compressed format. Check that your Eden build supports it; files are downloaded as-is and never converted.",
        "xci" => "Cartridge image format.",
        _ => string.Empty,
    };

    public bool HasFormatNote => FormatNote.Length > 0;

    partial void OnDescriptionChanged(string? value) => OnPropertyChanged(nameof(HasDescription));

    public async Task LoadAsync(IGameMetadataService metadata)
    {
        if (string.IsNullOrWhiteSpace(Item.TitleId)) return;
        IsLoading = true;
        try
        {
            var details = await metadata.GetDetailsAsync(Item.TitleId!, CancellationToken.None);
            if (details is null) return;
            Description = details.Description;
            foreach (var url in details.ScreenshotUrls.Take(6))
            {
                var path = await metadata.GetImageFileAsync(url, CancellationToken.None);
                var bitmap = await ImageLoader.LoadAsync(path, 640);
                if (bitmap is null) continue;
                Screenshots.Add(bitmap);
                OnPropertyChanged(nameof(HasScreenshots));
            }
        }
        catch (Exception)
        {
            // Metadata is optional.
        }
        finally
        {
            IsLoading = false;
        }
    }

    public string SizeText => Item.Size > 0 ? $"{SizeFormatter.Format(Item.Size)} ({Item.Size:N0} bytes)" : "Unknown";
}
