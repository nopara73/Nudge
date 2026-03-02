namespace Nudge.Ui.Services;

public sealed class NoOpLocalEpisodeSttTranscriber : ILocalEpisodeSttTranscriber
{
    public Task<string?> TranscribeFromAudioUrlAsync(string audioUrl, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }
}
