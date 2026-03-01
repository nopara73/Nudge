using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Linq;
using Nudge.Ui.ViewModels;

namespace Nudge.Ui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnBrowseConfigClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Select run config JSON",
                FileTypeFilter =
                [
                    new FilePickerFileType("JSON")
                    {
                        Patterns = ["*.json"]
                    }
                ]
            });

        var selected = files.FirstOrDefault();
        if (selected is null)
        {
            return;
        }

        vm.SetConfigPathFromUi(selected.Path.LocalPath);
    }
}