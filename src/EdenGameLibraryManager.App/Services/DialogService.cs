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

    /// <summary>A confirmation with one checkbox. Returns null when cancelled, otherwise whether the box was ticked.</summary>
    Task<bool?> ConfirmWithOptionAsync(string title, string message, string optionText, bool optionDefault, string confirmText, bool destructive = false);

    /// <summary>A destructive confirmation that only enables its button once <paramref name="requiredText"/> is typed exactly.</summary>
    Task<bool> ConfirmByTypingAsync(string title, string message, string requiredText, string confirmText);

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

    public async Task<bool?> ConfirmWithOptionAsync(string title, string message, string optionText, bool optionDefault, string confirmText, bool destructive = false)
    {
        if (Owner is null) return null;
        bool? result = null;
        var window = CreateWindow(title);

        var option = new CheckBox { Content = new TextBlock { Text = optionText, TextWrapping = TextWrapping.Wrap }, IsChecked = optionDefault };
        var confirm = new Button { Content = confirmText, IsDefault = !destructive, MinWidth = 96 };
        confirm.Classes.Add(destructive ? "danger" : "accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96 };
        confirm.Click += (_, _) => { result = option.IsChecked == true; window.Close(); };
        cancel.Click += (_, _) => window.Close();

        window.Content = Layout(message, [option], confirm, cancel);
        await window.ShowDialog(Owner);
        return result;
    }

    public async Task<bool> ConfirmByTypingAsync(string title, string message, string requiredText, string confirmText)
    {
        if (Owner is null) return false;
        var result = false;
        var window = CreateWindow(title);

        var prompt = new TextBlock { Text = $"Type {requiredText} to confirm.", FontWeight = FontWeight.SemiBold };
        var input = new TextBox { Watermark = requiredText };
        var confirm = new Button { Content = confirmText, MinWidth = 96, IsEnabled = false };
        confirm.Classes.Add("danger");
        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true, MinWidth = 96 };
        input.TextChanged += (_, _) => confirm.IsEnabled = string.Equals(input.Text?.Trim(), requiredText, StringComparison.Ordinal);
        confirm.Click += (_, _) => { result = confirm.IsEnabled; window.Close(); };
        cancel.Click += (_, _) => window.Close();
        window.Opened += (_, _) => input.Focus();

        window.Content = Layout(message, [prompt, input], confirm, cancel);
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

    private static Control Layout(string message, params Button[] buttons) => Layout(message, [], buttons);

    private static Control Layout(string message, Control[] extras, params Button[] buttons)
    {
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        foreach (var b in buttons) buttonRow.Children.Add(b);

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 20,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 412 } },
        };
        if (extras.Length > 0)
        {
            var group = new StackPanel { Spacing = 8 };
            foreach (var extra in extras) group.Children.Add(extra);
            panel.Children.Add(group);
        }
        panel.Children.Add(buttonRow);
        return panel;
    }
}
