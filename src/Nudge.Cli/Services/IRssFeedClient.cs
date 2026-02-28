namespace Nudge.Cli.Services;

public interface IRssFeedClient
{
    Task<string> GetFeedXmlAsync(string feedUrl, CancellationToken cancellationToken = default);
}
