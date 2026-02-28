namespace Nudge.Cli.Services;

public sealed class InMemoryRssFeedClient(IReadOnlyDictionary<string, string> seededFeeds) : IRssFeedClient
{
    private readonly IReadOnlyDictionary<string, string> _seededFeeds = seededFeeds;

    public Task<string> GetFeedXmlAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        if (_seededFeeds.TryGetValue(feedUrl, out var xml))
        {
            return Task.FromResult(xml);
        }

        throw new InvalidOperationException($"No in-memory RSS feed is registered for URL '{feedUrl}'.");
    }
}
