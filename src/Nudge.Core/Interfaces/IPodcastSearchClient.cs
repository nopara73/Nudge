using Nudge.Core.Models;

namespace Nudge.Core.Interfaces;

public interface IPodcastSearchClient
{
    Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(
        IReadOnlyList<string> searchTerms,
        int publishedAfterDays,
        int targetResultCount,
        CancellationToken cancellationToken = default);
}
