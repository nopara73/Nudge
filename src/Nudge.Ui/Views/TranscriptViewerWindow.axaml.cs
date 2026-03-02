using Avalonia.Controls;

namespace Nudge.Ui.Views;

public partial class TranscriptViewerWindow : Window
{
    public TranscriptViewerWindow()
    {
        InitializeComponent();
    }

    public TranscriptViewerWindow(string heading, string transcriptBody)
        : this()
    {
        TranscriptHeadingTextBlock.Text = heading;
        TranscriptBodyTextBox.Text = transcriptBody;
    }
}
