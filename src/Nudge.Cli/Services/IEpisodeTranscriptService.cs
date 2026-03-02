using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public interface IEpisodeTranscriptService
{
    Task<Episode> PopulateTranscriptAsync(
        Episode episode,
        IReadOnlyList<string> podcastHosts,
        CancellationToken cancellationToken = default);
}
