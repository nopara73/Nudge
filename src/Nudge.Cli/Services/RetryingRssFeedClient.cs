namespace Nudge.Cli.Services;

public sealed class RetryingRssFeedClient(IRssFeedClient innerClient) : IRssFeedClient
{
    private readonly IRssFeedClient _innerClient = innerClient;

    public async Task<string> GetFeedXmlAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _innerClient.GetFeedXmlAsync(feedUrl, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            return await _innerClient.GetFeedXmlAsync(feedUrl, cancellationToken);
        }
    }
}
