using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public interface IEpisodeSttTranscriber
{
    Task<string?> TranscribeAsync(Episode episode, CancellationToken cancellationToken = default);
}
