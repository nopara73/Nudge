using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Selection;
using Avalonia.VisualTree;
using System;
using System.ComponentModel;
using Nudge.Ui.Models;
using Nudge.Ui.ViewModels;

namespace Nudge.Ui.Views;

public partial class MainWindow : Window
{
    private const double DefaultWidth = 1200;
    private const double DefaultHeight = 800;
    private bool _isSynchronizingTrackerSelection;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (DataContext is MainWindowViewModel previousViewModel)
        {
            previousViewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        }

        base.OnDataContextChanged(e);

        if (DataContext is MainWindowViewModel currentViewModel)
        {
            currentViewModel.PropertyChanged += ViewModelOnPropertyChanged;
            SyncTrackerSelectionFromViewModel(currentViewModel.SelectedQueueItem);
        }
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

    private async void CopyFeedLinkMenuItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.SelectedQueueItem is null || string.IsNullOrWhiteSpace(viewModel.SelectedQueueItem.FeedUrl))
        {
            viewModel.RunStatus = "Feed URL is unavailable for this item.";
            return;
        }

        await CopyTextToClipboardAsync(viewModel.SelectedQueueItem.FeedUrl);
        viewModel.RunStatus = "Copied feed link to clipboard.";
    }

    private async void CopyEpisodeLinkMenuItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (sender is not MenuItem { DataContext: QueueEpisode episode } || string.IsNullOrWhiteSpace(episode.Url))
        {
            viewModel.RunStatus = "Episode link is unavailable for this item.";
            return;
        }

        await CopyTextToClipboardAsync(episode.Url);
        viewModel.RunStatus = "Copied episode link to clipboard.";
    }

    private async void ViewEpisodeTranscriptMenuItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await OpenEpisodeTranscriptAsync(sender, hostOnly: false);
    }

    private async void ViewEpisodeHostOnlyTranscriptMenuItem_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await OpenEpisodeTranscriptAsync(sender, hostOnly: true);
    }

    private void TrackerQueueListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var listBox = sender as ListBox;
        var selectedItem = listBox?.SelectedItem as QueueItem;

        if (_isSynchronizingTrackerSelection || DataContext is not MainWindowViewModel viewModel || selectedItem is null)
        {
            return;
        }

        if (!ReferenceEquals(viewModel.SelectedQueueItem, selectedItem))
        {
            viewModel.SelectedQueueItem = selectedItem;
        }

        SyncTrackerSelectionFromViewModel(selectedItem);
    }

    private void ShowTranscriptOptionsButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: QueueEpisode episode } button)
        {
            return;
        }

        var fullTranscriptMenuItem = new MenuItem
        {
            Header = "Full transcript",
            DataContext = episode
        };
        fullTranscriptMenuItem.Click += ViewEpisodeTranscriptMenuItem_OnClick;

        var hostOnlyTranscriptMenuItem = new MenuItem
        {
            Header = "Host-only lines",
            DataContext = episode
        };
        hostOnlyTranscriptMenuItem.Click += ViewEpisodeHostOnlyTranscriptMenuItem_OnClick;

        var chooserMenu = new ContextMenu
        {
            ItemsSource = new[]
            {
                fullTranscriptMenuItem,
                hostOnlyTranscriptMenuItem
            }
        };

        chooserMenu.Open(button);
    }

    private async Task OpenEpisodeTranscriptAsync(object? sender, bool hostOnly)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (sender is not Control { DataContext: QueueEpisode episode })
        {
            viewModel.RunStatus = "Episode transcript is unavailable for this item.";
            return;
        }

        var transcript = await viewModel.AcquireEpisodeTranscriptForViewingAsync(episode, hostOnly);
        if (transcript is null)
        {
            return;
        }

        var viewerWindow = new TranscriptViewerWindow(transcript.WindowTitle, transcript.Body);
        await viewerWindow.ShowDialog(this);
    }

    private async Task CopyTextToClipboardAsync(string value)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(value);
        }
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.SelectedQueueItem) || sender is not MainWindowViewModel viewModel)
        {
            return;
        }

        SyncTrackerSelectionFromViewModel(viewModel.SelectedQueueItem);
    }

    private void SyncTrackerSelectionFromViewModel(QueueItem? selectedItem)
    {
        _isSynchronizingTrackerSelection = true;

        try
        {
            foreach (var listBox in this.GetVisualDescendants().OfType<ListBox>())
            {
                if (listBox.DataContext is not MainWindowViewModel.QueueStateGroup)
                {
                    continue;
                }

                var matchingItem = listBox.ItemsSource?
                    .OfType<QueueItem>()
                    .FirstOrDefault(item => selectedItem is not null && item.IdentityKey == selectedItem.IdentityKey);

                listBox.SelectedItem = matchingItem;
            }
        }
        finally
        {
            _isSynchronizingTrackerSelection = false;
        }
    }

}