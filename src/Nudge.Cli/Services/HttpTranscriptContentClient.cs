namespace Nudge.Cli.Services;

public sealed class HttpTranscriptContentClient(HttpClient httpClient) : ITranscriptContentClient
{
    private readonly HttpClient _httpClient = httpClient;

    public async Task<string?> DownloadTranscriptAsync(string transcriptUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transcriptUrl))
        {
            return null;
        }

        using var response = await _httpClient.GetAsync(transcriptUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
