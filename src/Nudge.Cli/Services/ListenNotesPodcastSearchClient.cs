using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Nudge.Cli.Models;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class ListenNotesPodcastSearchClient(HttpClient httpClient, NudgeOptions options) : IPodcastSearchClient
{
    private const string SearchPath = "search";
    private const string UserAgent = "Nudge-Podcast-Bot/1.0";
    private readonly HttpClient _httpClient = httpClient;
    private readonly NudgeOptions _options = options;

    public async Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(
        IReadOnlyList<string> keywords,
        int publishedAfterDays,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await SearchWithRetryAsync(keywords, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<PodcastSearchResult>();
        }
        catch (Exception)
        {
            return Array.Empty<PodcastSearchResult>();
        }
    }

    private async Task<IReadOnlyList<PodcastSearchResult>> SearchWithRetryAsync(
        IReadOnlyList<string> keywords,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = BuildRequest(keywords);
            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadFromJsonAsync<ListenNotesSearchResponse>(cancellationToken: cancellationToken);
                    return MapResults(payload);
                }

                if (attempt == 0 && IsTransientStatusCode(response.StatusCode))
                {
                    await DelayForRetryAsync(response, cancellationToken);
                    continue;
                }

                return Array.Empty<PodcastSearchResult>();
            }
            catch (Exception ex) when (attempt == 0 && IsTransientException(ex, cancellationToken))
            {
                await DelayForRetryAsync(null, cancellationToken);
            }
        }

        return Array.Empty<PodcastSearchResult>();
    }

    private HttpRequestMessage BuildRequest(IReadOnlyList<string> keywords)
    {
        var query = Uri.EscapeDataString(string.Join(' ', keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())));
        var uri = $"{SearchPath}?type=podcast&len=50&q={query}";

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Add("X-ListenAPI-Key", _options.ApiKey);
        }

        return request;
    }

    private static bool IsTransientException(Exception exception, CancellationToken cancellationToken)
    {
        return exception switch
        {
            HttpRequestException => true,
            OperationCanceledException when !cancellationToken.IsCancellationRequested => true,
            _ => false
        };
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;
    }

    private static async Task DelayForRetryAsync(HttpResponseMessage? response, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(200);
        if (response is not null &&
            response.StatusCode == HttpStatusCode.TooManyRequests &&
            response.Headers.RetryAfter?.Delta is { } retryAfterDelta &&
            retryAfterDelta > TimeSpan.Zero)
        {
            delay = retryAfterDelta;
        }

        await Task.Delay(delay, cancellationToken);
    }

    private static IReadOnlyList<PodcastSearchResult> MapResults(ListenNotesSearchResponse? payload)
    {
        if (payload?.Results is null || payload.Results.Count == 0)
        {
            return Array.Empty<PodcastSearchResult>();
        }

        return payload.Results
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Where(r => !string.IsNullOrWhiteSpace(r.Rss))
            .DistinctBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new PodcastSearchResult
            {
                Id = $"listennotes:{r.Id}",
                Name = r.TitleOriginal ?? string.Empty,
                Description = r.DescriptionOriginal ?? string.Empty,
                FeedUrl = r.Rss!,
                EstimatedReach = NormalizeReach(r.ListenScore)
            })
            .ToArray();
    }

    private static double NormalizeReach(double? listenScore)
    {
        if (!listenScore.HasValue)
        {
            return 0.5;
        }

        return Math.Clamp(listenScore.Value / 100.0, 0.0, 1.0);
    }

    private sealed record ListenNotesSearchResponse
    {
        public List<ListenNotesPodcastResult> Results { get; init; } = [];
    }

    private sealed record ListenNotesPodcastResult
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("title_original")]
        public string? TitleOriginal { get; init; }

        [JsonPropertyName("description_original")]
        public string? DescriptionOriginal { get; init; }

        [JsonPropertyName("rss")]
        public string? Rss { get; init; }

        [JsonPropertyName("listen_score")]
        public double? ListenScore { get; init; }
    }
}
