using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nudge.Cli.Models;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class ListenNotesPodcastSearchClient(HttpClient httpClient, NudgeOptions options) : IPodcastSearchClient
{
    private const string SearchPath = "graphql";
    private const string UserAgent = "Nudge-Podcast-Bot/1.0";
    private const int MaxResults = 50;
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
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var mapped = MapResults(content);
                    return mapped;
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
        var searchTerm = string.Join(' ', keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()));
        var payload = BuildGraphQlPayload(searchTerm);

        var request = new HttpRequestMessage(HttpMethod.Post, SearchPath);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey.Trim());
        }
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        return request;
    }

    private static string BuildGraphQlPayload(string searchTerm)
    {
        var query =
            """
            query SearchPodcasts($searchTerm: String, $first: Int!) {
              podcasts(searchTerm: $searchTerm, first: $first) {
                data {
                  id
                  title
                  description
                  language
                  rssUrl
                  audienceEstimate
                  powerScore
                }
              }
            }
            """;

        var payload = new
        {
            query,
            variables = new
            {
                searchTerm = string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm,
                first = MaxResults
            }
        };

        return JsonSerializer.Serialize(payload);
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

    private static IReadOnlyList<PodcastSearchResult> MapResults(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<PodcastSearchResult>();
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var sourceItems = ExtractPodcastItems(document.RootElement);
            var mapped = sourceItems
                .Select(TryMapPodcast)
                .Where(static mappedItem => mappedItem is not null)
                .Select(static mappedItem => mappedItem!)
                .DistinctBy(static r => r.Id, StringComparer.Ordinal)
                .ToArray();

            return mapped;
        }
        catch (JsonException)
        {
            return Array.Empty<PodcastSearchResult>();
        }
    }

    private static IEnumerable<JsonElement> ExtractPodcastItems(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (TryGetPropertyIgnoreCase(root, "data", out var dataNode) &&
            dataNode.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(dataNode, "podcasts", out var podcastsNode) &&
            podcastsNode.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(podcastsNode, "data", out var podcastDataNode) &&
            podcastDataNode.ValueKind == JsonValueKind.Array)
        {
            return podcastDataNode.EnumerateArray();
        }

        var fallback = new List<JsonElement>();
        CollectCandidatePodcastObjects(root, fallback);
        return fallback;
    }

    private static void CollectCandidatePodcastObjects(JsonElement node, List<JsonElement> results)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (LooksLikePodcastObject(node))
                {
                    results.Add(node);
                }

                foreach (var property in node.EnumerateObject())
                {
                    CollectCandidatePodcastObjects(property.Value, results);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    CollectCandidatePodcastObjects(item, results);
                }

                break;
        }
    }

    private static bool LooksLikePodcastObject(JsonElement node)
    {
        return TryGetString(node, "id", out _) &&
               (TryGetString(node, "title", out _) || TryGetString(node, "name", out _));
    }

    private static PodcastSearchResult? TryMapPodcast(JsonElement item)
    {
        if (!TryGetString(item, "id", out var id))
        {
            return null;
        }

        if (!TryGetString(item, "rssUrl", out var rssUrl) &&
            !TryGetString(item, "rss", out rssUrl) &&
            !TryGetString(item, "feedUrl", out rssUrl))
        {
            return null;
        }

        var title = TryGetString(item, "title", out var itemTitle)
            ? itemTitle
            : TryGetString(item, "name", out var itemName) ? itemName : string.Empty;
        var description = TryGetString(item, "description", out var itemDescription)
            ? itemDescription
            : string.Empty;
        var language = TryGetString(item, "language", out var itemLanguage)
            ? itemLanguage
            : TryGetString(item, "languageCode", out var itemLanguageCode) ? itemLanguageCode : null;
        var estimatedReach = NormalizeReach(
            TryGetDouble(item, "audienceEstimate", out var audienceEstimate) ? audienceEstimate : null,
            TryGetDouble(item, "powerScore", out var powerScore) ? powerScore : null);

        return new PodcastSearchResult
        {
            Id = $"podchaser:{id}",
            Name = title,
            Description = description,
            Language = language,
            FeedUrl = rssUrl,
            EstimatedReach = estimatedReach
        };
    }

    private static double NormalizeReach(double? audienceEstimate, double? powerScore)
    {
        if (audienceEstimate.HasValue && audienceEstimate.Value > 0)
        {
            var normalizedFromAudience = Math.Log10(audienceEstimate.Value + 1) / 7.0;
            return Math.Clamp(normalizedFromAudience, 0.0, 1.0);
        }

        if (powerScore.HasValue)
        {
            return Math.Clamp(powerScore.Value / 100.0, 0.0, 1.0);
        }

        return 0.5;
    }

    private static bool TryGetString(JsonElement node, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyIgnoreCase(node, propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = property.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        value = raw.Trim();
        return true;
    }

    private static bool TryGetDouble(JsonElement node, string propertyName, out double value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(node, propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.String &&
            double.TryParse(property.GetString(), out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement node, string propertyName, out JsonElement value)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
