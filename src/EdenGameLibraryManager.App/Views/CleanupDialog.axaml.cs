using Avalonia.Controls;
using EdenGameLibraryManager.App.ViewModels;

namespace EdenGameLibraryManager.App.Views;

public partial class CleanupDialog : Window
{
    public CleanupDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CleanupDialogViewModel vm)
                vm.CloseRequested += (_, result) => Close(result);
        };
    }
}
