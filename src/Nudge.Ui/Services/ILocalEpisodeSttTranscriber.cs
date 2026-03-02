namespace Nudge.Ui.Services;

public interface ILocalEpisodeSttTranscriber
{
    Task<string?> TranscribeFromAudioUrlAsync(string audioUrl, CancellationToken cancellationToken = default);
}
