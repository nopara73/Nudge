using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using System.Linq;
using Nudge.Ui.ViewModels;

namespace Nudge.Ui.Views;

public partial class MainWindow : Window
{
    private const double DefaultWidth = 1200;
    private const double DefaultHeight = 800;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        Width = DefaultWidth;
        Height = DefaultHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var workingArea = screen.WorkingArea;
        Position = new PixelPoint(
            workingArea.X + workingArea.Width - (int)Width,
            workingArea.Y);
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