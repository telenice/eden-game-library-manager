using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using EdenGameLibraryManager.App.ViewModels;
using EdenGameLibraryManager.App.Views;

namespace EdenGameLibraryManager.App.Services;

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false);
    Task ShowMessageAsync(string title, string message);
    Task<string?> PickFolderAsync(string title, string? startPath);

    /// <summary>Shows the cleanup picker; returns true when the selected games were deleted.</summary>
    Task<bool> ShowCleanupAsync(CleanupDialogViewModel viewModel);
}

/// <summary>Modal dialogs owned by the main window.</summary>
public sealed class DialogService : IDialogService
{
    public Window? Owner { get; set; }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool destructive = false)
    {
        if (Owner is null) return false;
        var result = false;
        var window = CreateWindow(title);

        var confirm = new Button { Content = confirmText, IsDefault = !destructive, MinWidth = 96 };
        if (destructive) confirm.Classes.Add("danger");
        else confirm.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        confirm.Click += (_, _) => { result = true; window.Close(); };
        cancel.Click += (_, _) => window.Close();

        window.Content = Layout(message, confirm, cancel);
        await window.ShowDialog(Owner);
        return result;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (Owner is null) return;
        var window = CreateWindow(title);
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 96 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => window.Close();
        window.Content = Layout(message, ok);
        await window.ShowDialog(Owner);
    }

    public async Task<string?> PickFolderAsync(string title, string? startPath)
    {
        if (Owner is null) return null;
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath))
            start = await Owner.StorageProvider.TryGetFolderFromPathAsync(startPath);

        var result = await Owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ShowCleanupAsync(CleanupDialogViewModel viewModel)
    {
        if (Owner is null) return false;
        var window = new CleanupDialog { DataContext = viewModel };
        return await window.ShowDialog<bool>(Owner);
    }

    private static Window CreateWindow(string title) => new()
    {
        Title = title,
        Width = 460,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    private static Control Layout(string message, params Button[] buttons)
    {
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        foreach (var b in buttons) buttonRow.Children.Add(b);

        return new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 412 },
                buttonRow,
            },
        };
    }
}
