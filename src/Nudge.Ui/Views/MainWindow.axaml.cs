using Avalonia;
using Avalonia.Controls;
using System;

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
        WindowStartupLocation = WindowStartupLocation.Manual;

        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
        {
            return;
        }

        var workingArea = screen.WorkingArea;
        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;

        // Window Width/Height are in DIPs, but WorkingArea is in physical pixels.
        var maxWidthDip = workingArea.Width / scaling;
        var maxHeightDip = workingArea.Height / scaling;
        var targetWidthDip = Math.Min(DefaultWidth, maxWidthDip);
        var targetHeightDip = Math.Min(DefaultHeight, maxHeightDip);

        Width = targetWidthDip;
        Height = targetHeightDip;

        var targetWidthPx = (int)Math.Round(targetWidthDip * scaling);
        var targetX = workingArea.X + Math.Max(0, workingArea.Width - targetWidthPx);
        var targetY = workingArea.Y;

        Position = new PixelPoint(
            targetX,
            targetY);
    }

}