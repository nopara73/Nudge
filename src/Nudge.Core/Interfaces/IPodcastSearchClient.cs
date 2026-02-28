using Nudge.Core.Models;

namespace Nudge.Core.Interfaces;

public interface IPodcastSearchClient
{
    Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(
        IReadOnlyList<string> keywords,
        int publishedAfterDays,
        CancellationToken cancellationToken = default);
}
