using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class NoOpEpisodeSttTranscriber : IEpisodeSttTranscriber
{
    public Task<string?> TranscribeAsync(Episode episode, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<string?>(null);
    }
}
