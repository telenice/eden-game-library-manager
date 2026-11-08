using EdenGameLibraryManager.Core.Models;
using EdenGameLibraryManager.Core.Services;

namespace EdenGameLibraryManager.App.ViewModels;

/// <summary>A base game with its updates and DLC, shown as one card when "Group by game" is on.</summary>
public sealed class GameGroupViewModel : ViewModelBase
{
    public GameGroupViewModel(string key, IReadOnlyList<GameItemViewModel> members, IReadOnlyList<GameItemViewModel> pending)
    {
        Key = key;
        Members = members;
        Pending = pending;
        Base = members.FirstOrDefault(m => m.IsBase);
    }

    public string Key { get; }

    /// <summary>All catalogue entries for this title, base first.</summary>
    public IReadOnlyList<GameItemViewModel> Members { get; }

    /// <summary>Entries that pass the current filters and still need downloading.</summary>
    public IReadOnlyList<GameItemViewModel> Pending { get; }

    public GameItemViewModel? Base { get; }
    public GameItemViewModel Primary => Base ?? Members[0];

    public string Name => Base?.Name
                          ?? Members.Select(m => m.Item.TitleName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                          ?? Primary.Name;

    public int UpdateCount => Members.Count(m => m.Item.ContentType == ContentType.Update);
    public int DlcCount => Members.Count(m => m.Item.ContentType == ContentType.Dlc);
    public long PendingBytes => Pending.Sum(p => p.Item.Size);
    public long MaxFileId => Members.Max(m => m.Item.FileId ?? 0);

    public string Summary
    {
        get
        {
            var parts = new List<string> { Base is null ? "Base game not on server" : "Base game" };
            if (UpdateCount > 0) parts.Add(UpdateCount == 1 ? "1 update" : $"{UpdateCount} updates");
            if (DlcCount > 0) parts.Add($"{DlcCount} DLC");
            return string.Join(" · ", parts);
        }
    }

    public string StatusText => Pending.Count > 0
        ? $"{Pending.Count} to download · {SizeFormatter.Format(PendingBytes)}"
        : Members.Any(m => m.IsQueued) ? "Queued" : "Everything downloaded";

    /// <summary>The base game is already local but a newer update (or DLC) is waiting.</summary>
    public bool HasNewContent => Base?.IsDownloaded == true && Pending.Count > 0;

    public string NewContentText => Pending.Any(p => p.Item.ContentType == ContentType.Update) ? "Update available" : "New DLC";

    public bool HasCover => Primary.HasCover;
    public bool CanDownload => Pending.Count > 0;

    public string DownloadButtonText => Pending.Count switch
    {
        0 => Members.Any(m => m.IsQueued) ? "Queued" : "Downloaded",
        1 => "Download",
        _ => $"Download {Pending.Count}",
    };
}
