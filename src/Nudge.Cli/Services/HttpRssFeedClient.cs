using System.Net.Http.Headers;

namespace Nudge.Cli.Services;

public sealed class HttpRssFeedClient(HttpClient httpClient) : IRssFeedClient
{
    private const string UserAgent = "Nudge-Podcast-Bot/1.0";
    private readonly HttpClient _httpClient = httpClient;

    public async Task<string> GetFeedXmlAsync(string feedUrl, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
