using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Nudge.Cli.Models;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class ListenNotesPodcastSearchClient(
    HttpClient httpClient,
    NudgeOptions options,
    PodchaserSearchCache? searchCache = null,
    string? tokenScope = null) : IPodcastSearchClient
{
    private const string SearchPath = "graphql";
    private const string UserAgent = "Nudge-Podcast-Bot/1.0";
    private const int MaxResults = 50;
    private const int SinglePageMaxResults = 25;
    private const int LowCostMaxResults = 10;
    private const int MaxPagesPerTerm = 3;
    private const int MinCandidateTarget = 15;
    private const int MaxCandidateTarget = 90;
    private const int FirstPageIndex = 0;
    private const string SearchPodcastsQuery =
        """
        query SearchPodcasts($searchTerm: String, $first: Int!, $page: Int!) {
          podcasts(searchTerm: $searchTerm, first: $first, page: $page) {
            data {
              id
              title
              description
              language
              rssUrl
              audienceEstimate
              episodeAudienceEstimate {
                from
                to
              }
              powerScore
              socialFollowerCounts {
                youtube
                twitter
                instagram
                linkedin
                tiktok
                facebook
                patreon
                twitch
              }
            }
          }
        }
        """;
    private const string SearchPodcastsLegacyQuery =
        """
        query SearchPodcasts($searchTerm: String, $first: Int!, $page: Int!) {
          podcasts(searchTerm: $searchTerm, first: $first, page: $page) {
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
    private readonly HttpClient _httpClient = httpClient;
    private readonly NudgeOptions _options = options;
    private readonly PodchaserSearchCache? _searchCache = searchCache;
    private readonly string _tokenScope = string.IsNullOrWhiteSpace(tokenScope) ? "default" : tokenScope.Trim();
    private PodchaserSearchDiagnostics _lastSearchDiagnostics = PodchaserSearchDiagnostics.Empty;
    private int _pointBudgetExceeded;
    private int _tokenRejected;

    public bool WasPointBudgetExceeded => Volatile.Read(ref _pointBudgetExceeded) == 1;
    public bool WasTokenRejected => Volatile.Read(ref _tokenRejected) == 1;
    public PodchaserSearchDiagnostics LastSearchDiagnostics => _lastSearchDiagnostics;

    public async Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(
        IReadOnlyList<string> keywords,
        int publishedAfterDays,
        int targetResultCount,
        CancellationToken cancellationToken = default)
    {
        var execution = new SearchExecutionState();
        try
        {
            var results = await SearchWithRetryAsync(keywords, publishedAfterDays, targetResultCount, execution, cancellationToken);
            execution.RawCandidatesReturned = results.Count;
            _lastSearchDiagnostics = execution.ToDiagnostics();
            return results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _lastSearchDiagnostics = execution.ToDiagnostics();
            return Array.Empty<PodcastSearchResult>();
        }
        catch (Exception)
        {
            _lastSearchDiagnostics = execution.ToDiagnostics();
            return Array.Empty<PodcastSearchResult>();
        }
    }

    private async Task<IReadOnlyList<PodcastSearchResult>> SearchWithRetryAsync(
        IReadOnlyList<string> keywords,
        int publishedAfterDays,
        int targetResultCount,
        SearchExecutionState execution,
        CancellationToken cancellationToken)
    {
        var terms = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (terms.Length == 0)
        {
            terms = [string.Empty];
        }
        var budget = BuildBudget(terms.Length, targetResultCount);
        execution.Executed = true;
        execution.KeywordCount = terms.Length;
        execution.TargetResultCount = Math.Max(1, targetResultCount);
        execution.TargetCandidateCount = budget.TargetCandidateCount;
        // #region agent log
        WriteDebugLog(
            hypothesisId: "H2_page_start_or_empty_terms",
            location: "ListenNotesPodcastSearchClient.cs:SearchWithRetryAsync",
            message: "Prepared deduplicated search terms.",
            data: new
            {
                rawKeywordsCount = keywords.Count,
                dedupedTermsCount = terms.Length,
                firstTerm = terms[0],
                firstPageIndex = FirstPageIndex
            },
            runId: "initial");
        // #endregion

        var cacheKey = BuildCacheKey(terms, publishedAfterDays, budget.TargetCandidateCount);
        if (_searchCache is not null && _searchCache.TryGet(cacheKey, out var cachedResults))
        {
            execution.CacheHit = true;
            execution.RawCandidatesReturned = cachedResults.Count;
            return cachedResults;
        }

        var accumulated = new List<PodcastSearchResult>(budget.TargetCandidateCount);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var includeExtendedSignals = true;
        foreach (var term in terms)
        {
            var (termResults, switchedToLegacy) = await SearchTermWithPagingAsync(
                term,
                includeExtendedSignals,
                budget,
                execution,
                cancellationToken);
            // #region agent log
            WriteDebugLogB9(
                hypothesisId: "H1_search_broad_term_matching",
                location: "ListenNotesPodcastSearchClient.cs:SearchWithRetryAsync",
                message: "Completed search term aggregation before dedupe merge.",
                data: new
                {
                    term,
                    termResultsCount = termResults.Count,
                    sampleShowNames = termResults
                        .Take(5)
                        .Select(r => r.Name)
                        .ToArray()
                },
                runId: "initial");
            // #endregion
            if (switchedToLegacy)
            {
                includeExtendedSignals = false;
            }

            foreach (var result in termResults)
            {
                if (!seenIds.Add(result.Id))
                {
                    continue;
                }

                accumulated.Add(result);
                if (accumulated.Count >= budget.TargetCandidateCount)
                {
                    execution.EarlyExitTriggered = true;
                    var boundedResults = accumulated.Take(budget.TargetCandidateCount).ToArray();
                    _searchCache?.Set(cacheKey, boundedResults);
                    return boundedResults;
                }
            }
        }

        _searchCache?.Set(cacheKey, accumulated);
        return accumulated;
    }

    private async Task<(IReadOnlyList<PodcastSearchResult> Results, bool SwitchedToLegacy)> SearchTermWithPagingAsync(
        string searchTerm,
        bool includeExtendedSignals,
        SearchBudget budget,
        SearchExecutionState execution,
        CancellationToken cancellationToken)
    {
        var termResults = new List<PodcastSearchResult>();
        var seenTermIds = new HashSet<string>(StringComparer.Ordinal);
        var switchedToLegacy = false;
        var pageSize = budget.InitialPageSize;
        var page = FirstPageIndex;
        while (page < FirstPageIndex + budget.MaxPagesPerTerm)
        {
            var (mapped, sourceItemCount, shouldRetryWithLegacyQuery, shouldRetryAttempt, shouldRetryWithLowerPageSize) = await ExecuteSearchAsync(
                searchTerm,
                includeExtendedSignals,
                page,
                pageSize,
                execution,
                cancellationToken);

            if (shouldRetryWithLegacyQuery && includeExtendedSignals)
            {
                // #region agent log
                WriteDebugLog(
                    hypothesisId: "H3_legacy_fallback",
                    location: "ListenNotesPodcastSearchClient.cs:SearchTermWithPagingAsync",
                    message: "Switching to legacy query after GraphQL errors.",
                    data: new { searchTerm, page, includeExtendedSignalsBeforeSwitch = true },
                    runId: "initial");
                // #endregion
                execution.LegacyFallbackTriggered = true;
                includeExtendedSignals = false;
                switchedToLegacy = true;
                termResults.Clear();
                seenTermIds.Clear();
                page = FirstPageIndex;
                continue;
            }

            if (shouldRetryAttempt)
            {
                continue;
            }

            if (shouldRetryWithLowerPageSize && pageSize > LowCostMaxResults)
            {
                // #region agent log
                WriteDebugLog(
                    hypothesisId: "H6_budget_fallback",
                    location: "ListenNotesPodcastSearchClient.cs:SearchTermWithPagingAsync",
                    message: "Lowering page size after API points budget response.",
                    data: new { searchTerm, previousPageSize = pageSize, newPageSize = LowCostMaxResults },
                    runId: "initial");
                // #endregion
                execution.ReducedPageSizeTriggered = true;
                pageSize = LowCostMaxResults;
                termResults.Clear();
                seenTermIds.Clear();
                page = FirstPageIndex;
                continue;
            }

            foreach (var result in mapped)
            {
                if (seenTermIds.Add(result.Id))
                {
                    termResults.Add(result);
                }
            }

            var likelyViableCandidateCount = CountLikelyViableCandidates(termResults, searchTerm);
            if (sourceItemCount < pageSize ||
                termResults.Count >= budget.PerTermCandidateCount ||
                likelyViableCandidateCount >= budget.PerTermLikelyViableCandidateCount)
            {
                // #region agent log
                WriteDebugLog(
                    hypothesisId: "H5_pagination_break",
                    location: "ListenNotesPodcastSearchClient.cs:SearchTermWithPagingAsync",
                    message: "Pagination loop break condition reached.",
                    data: new
                    {
                        searchTerm,
                        page,
                        pageSize,
                        sourceItemCount,
                        mappedCount = mapped.Count,
                        termResultsCount = termResults.Count,
                        maxResults = MaxResults,
                        likelyViableCandidateCount,
                        targetCandidateCount = budget.PerTermCandidateCount
                    },
                    runId: "initial");
                // #endregion
                if (sourceItemCount >= pageSize &&
                    (termResults.Count >= budget.PerTermCandidateCount ||
                     likelyViableCandidateCount >= budget.PerTermLikelyViableCandidateCount))
                {
                    execution.EarlyExitTriggered = true;
                }

                break;
            }

            page++;
        }

        return (termResults, switchedToLegacy);
    }

    private async Task<(IReadOnlyList<PodcastSearchResult> Results, int SourceItemCount, bool ShouldRetryWithLegacyQuery, bool ShouldRetryAttempt, bool ShouldRetryWithLowerPageSize)> ExecuteSearchAsync(
        string searchTerm,
        bool includeExtendedSignals,
        int page,
        int pageSize,
        SearchExecutionState execution,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = BuildRequest(searchTerm, includeExtendedSignals, page, pageSize);
            try
            {
                execution.HttpRequestsSent++;
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    execution.SuccessfulPageCount++;
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var (mapped, sourceItemCount) = MapResults(content);
                    var shouldRetryWithLegacyQuery = includeExtendedSignals && HasGraphQlErrors(content);
                    // #region agent log
                    WriteDebugLog(
                        hypothesisId: "H2_H3_H4_mapping",
                        location: "ListenNotesPodcastSearchClient.cs:ExecuteSearchAsync",
                        message: "GraphQL search response processed.",
                        data: new
                        {
                            searchTerm,
                            includeExtendedSignals,
                            page,
                            pageSize,
                            statusCode = (int)response.StatusCode,
                            sourceItemCount,
                            mappedCount = mapped.Count,
                            shouldRetryWithLegacyQuery
                        },
                        runId: "initial");
                    // #endregion
                    return (mapped, sourceItemCount, shouldRetryWithLegacyQuery, false, false);
                }

                if (attempt == 0 && IsTransientStatusCode(response.StatusCode))
                {
                    // #region agent log
                    WriteDebugLog(
                        hypothesisId: "H2_H3_http_status",
                        location: "ListenNotesPodcastSearchClient.cs:ExecuteSearchAsync",
                        message: "Transient status encountered, retrying request.",
                        data: new
                        {
                            searchTerm,
                            includeExtendedSignals,
                            page,
                            pageSize,
                            statusCode = (int)response.StatusCode,
                            attempt
                        },
                        runId: "initial");
                    // #endregion
                    await DelayForRetryAsync(response, cancellationToken);
                    continue;
                }

                // #region agent log
                var errorBody = response.Content is null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(cancellationToken);
                var pointsExceeded = IsPointBudgetExceeded(response.StatusCode, errorBody);
                var tokenRejected = IsTokenRejectedStatusCode(response.StatusCode);
                if (pointsExceeded)
                {
                    Interlocked.Exchange(ref _pointBudgetExceeded, 1);
                }
                if (tokenRejected)
                {
                    Interlocked.Exchange(ref _tokenRejected, 1);
                }
                WriteDebugLog(
                    hypothesisId: "H2_H3_http_status",
                    location: "ListenNotesPodcastSearchClient.cs:ExecuteSearchAsync",
                    message: "Non-success status returned without retry.",
                    data: new
                    {
                        searchTerm,
                        includeExtendedSignals,
                        page,
                        pageSize,
                        statusCode = (int)response.StatusCode,
                        attempt,
                        pointsExceeded,
                        tokenRejected,
                        errorBodySnippet = errorBody.Length > 400 ? errorBody[..400] : errorBody
                    },
                    runId: "initial");
                // #endregion
                if (pointsExceeded && includeExtendedSignals)
                {
                    return (Array.Empty<PodcastSearchResult>(), 0, true, false, false);
                }

                if (pointsExceeded && pageSize > LowCostMaxResults)
                {
                    return (Array.Empty<PodcastSearchResult>(), 0, false, false, true);
                }

                return (Array.Empty<PodcastSearchResult>(), 0, false, false, false);
            }
            catch (Exception ex) when (attempt == 0 && IsTransientException(ex, cancellationToken))
            {
                // #region agent log
                WriteDebugLog(
                    hypothesisId: "H2_H3_http_status",
                    location: "ListenNotesPodcastSearchClient.cs:ExecuteSearchAsync",
                    message: "Transient exception encountered, retrying request.",
                    data: new
                    {
                        searchTerm,
                        includeExtendedSignals,
                        page,
                        pageSize,
                        attempt,
                        exceptionType = ex.GetType().Name
                    },
                    runId: "initial");
                // #endregion
                await DelayForRetryAsync(null, cancellationToken);
            }
        }

        // #region agent log
        WriteDebugLog(
            hypothesisId: "H2_H3_http_status",
            location: "ListenNotesPodcastSearchClient.cs:ExecuteSearchAsync",
            message: "Retries exhausted; returning empty with retry marker.",
            data: new { searchTerm, includeExtendedSignals, page, pageSize },
            runId: "initial");
        // #endregion
        return (Array.Empty<PodcastSearchResult>(), 0, false, true, false);
    }

    private HttpRequestMessage BuildRequest(string searchTerm, bool includeExtendedSignals, int page, int pageSize)
    {
        var payload = BuildGraphQlPayload(searchTerm, includeExtendedSignals, page, pageSize);

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

    private static string BuildGraphQlPayload(string searchTerm, bool includeExtendedSignals, int page, int pageSize)
    {
        var query = includeExtendedSignals ? SearchPodcastsQuery : SearchPodcastsLegacyQuery;

        var payload = new
        {
            query,
            variables = new
            {
                searchTerm = string.IsNullOrWhiteSpace(searchTerm) ? null : searchTerm,
                first = pageSize,
                page
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private string BuildCacheKey(IReadOnlyList<string> terms, int publishedAfterDays, int targetCandidateCount)
    {
        var normalizedTerms = string.Join(
            '|',
            terms.Select(static term => string.IsNullOrWhiteSpace(term) ? string.Empty : term.Trim().ToLowerInvariant()));

        return $"{_tokenScope}|{_options.BaseUrl.Trim()}|{publishedAfterDays}|{targetCandidateCount}|{normalizedTerms}";
    }

    private static SearchBudget BuildBudget(int keywordCount, int targetResultCount)
    {
        var normalizedKeywordCount = Math.Max(1, keywordCount);
        var normalizedTargetCount = Math.Max(1, targetResultCount);
        var targetCandidateCount = Math.Clamp(
            Math.Max(normalizedTargetCount * 5, MinCandidateTarget),
            MinCandidateTarget,
            MaxCandidateTarget);
        var maxPagesPerTerm = normalizedTargetCount <= 15
            ? 1
            : normalizedTargetCount <= 40 ? 2 : MaxPagesPerTerm;
        var initialPageSize = maxPagesPerTerm == 1 ? SinglePageMaxResults : MaxResults;
        var perTermCandidateCount = Math.Clamp(
            (int)Math.Ceiling((double)targetCandidateCount / normalizedKeywordCount) + 2,
            6,
            initialPageSize * maxPagesPerTerm);
        var perTermLikelyViableCandidateCount = Math.Clamp(
            (int)Math.Ceiling((double)normalizedTargetCount / normalizedKeywordCount) + 1,
            3,
            perTermCandidateCount);

        return new SearchBudget(
            targetCandidateCount,
            maxPagesPerTerm,
            initialPageSize,
            perTermCandidateCount,
            perTermLikelyViableCandidateCount);
    }

    private static int CountLikelyViableCandidates(IEnumerable<PodcastSearchResult> results, string searchTerm)
    {
        return results.Count(result =>
            !IsExplicitlyUnsupportedLanguage(result.Language) &&
            HasBasicTermAlignment(result, searchTerm));
    }

    private static bool HasBasicTermAlignment(PodcastSearchResult result, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return true;
        }

        var normalizedSearchTerm = NormalizeSearchText(searchTerm);
        if (string.IsNullOrWhiteSpace(normalizedSearchTerm))
        {
            return true;
        }

        var corpus = NormalizeSearchText($"{result.Name} {result.Description}");
        if (string.IsNullOrWhiteSpace(corpus))
        {
            return false;
        }

        if (corpus.Contains(normalizedSearchTerm, StringComparison.Ordinal))
        {
            return true;
        }

        var termTokens = normalizedSearchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return termTokens.Length == 1 && corpus.Contains(termTokens[0], StringComparison.Ordinal);
    }

    private static bool IsExplicitlyUnsupportedLanguage(string? rawLanguage)
    {
        if (string.IsNullOrWhiteSpace(rawLanguage))
        {
            return false;
        }

        var normalized = rawLanguage.Trim().ToLowerInvariant().Replace('_', '-');
        return !(normalized.StartsWith("en", StringComparison.Ordinal) ||
                 normalized.StartsWith("hu", StringComparison.Ordinal) ||
                 normalized.Contains("english", StringComparison.Ordinal) ||
                 normalized.Contains("hungarian", StringComparison.Ordinal));
    }

    private static string NormalizeSearchText(string value)
    {
        return string.Join(
            ' ',
            (value ?? string.Empty)
                .ToLowerInvariant()
                .Split(
                    [' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '|', '-', '_', '+', '=', '*', '&', '#', '@', '%', '^', '$', '<', '>', '~', '`'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private sealed record SearchBudget(
        int TargetCandidateCount,
        int MaxPagesPerTerm,
        int InitialPageSize,
        int PerTermCandidateCount,
        int PerTermLikelyViableCandidateCount);

    private sealed class SearchExecutionState
    {
        public bool Executed { get; set; }
        public int KeywordCount { get; set; }
        public int TargetResultCount { get; set; }
        public int TargetCandidateCount { get; set; }
        public int HttpRequestsSent { get; set; }
        public int SuccessfulPageCount { get; set; }
        public int RawCandidatesReturned { get; set; }
        public bool CacheHit { get; set; }
        public bool LegacyFallbackTriggered { get; set; }
        public bool ReducedPageSizeTriggered { get; set; }
        public bool EarlyExitTriggered { get; set; }

        public PodchaserSearchDiagnostics ToDiagnostics()
        {
            return new PodchaserSearchDiagnostics
            {
                Executed = Executed,
                KeywordCount = KeywordCount,
                TargetResultCount = TargetResultCount,
                TargetCandidateCount = TargetCandidateCount,
                HttpRequestsSent = HttpRequestsSent,
                SuccessfulPageCount = SuccessfulPageCount,
                RawCandidatesReturned = RawCandidatesReturned,
                CacheHit = CacheHit,
                LegacyFallbackTriggered = LegacyFallbackTriggered,
                ReducedPageSizeTriggered = ReducedPageSizeTriggered,
                EarlyExitTriggered = EarlyExitTriggered
            };
        }
    }

    private static bool HasGraphQlErrors(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryGetPropertyIgnoreCase(document.RootElement, "errors", out var errorsNode) &&
                   errorsNode.ValueKind == JsonValueKind.Array &&
                   errorsNode.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static bool IsPointBudgetExceeded(HttpStatusCode statusCode, string body)
    {
        return statusCode == HttpStatusCode.BadRequest &&
               body.Contains("exceed your remaining points", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTokenRejectedStatusCode(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
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

    private static (IReadOnlyList<PodcastSearchResult> Results, int SourceItemCount) MapResults(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (Array.Empty<PodcastSearchResult>(), 0);
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var sourceItems = ExtractPodcastItems(document.RootElement).ToArray();
            var mapped = sourceItems
                .Select(TryMapPodcast)
                .Where(static mappedItem => mappedItem is not null)
                .Select(static mappedItem => mappedItem!)
                .DistinctBy(static r => r.Id, StringComparer.Ordinal)
                .ToArray();

            return (mapped, sourceItems.Length);
        }
        catch (JsonException)
        {
            return (Array.Empty<PodcastSearchResult>(), 0);
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
        var episodeAudienceMidpoint = TryGetRangeMidpoint(item, "episodeAudienceEstimate", out var midpoint)
            ? (double?)midpoint
            : null;
        var socialFollowerCount = TryGetFollowerCount(item, out var followerCount)
            ? (double?)followerCount
            : null;
        var estimatedReach = NormalizeReach(
            TryGetDouble(item, "audienceEstimate", out var audienceEstimate) ? audienceEstimate : null,
            TryGetDouble(item, "powerScore", out var powerScore) ? powerScore : null,
            episodeAudienceMidpoint,
            socialFollowerCount);

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

    private static double NormalizeReach(
        double? audienceEstimate,
        double? powerScore,
        double? episodeAudienceMidpoint,
        double? socialFollowerCount)
    {
        var weightedValue = 0.0;
        var weightTotal = 0.0;

        if (audienceEstimate.HasValue && audienceEstimate.Value > 0)
        {
            var normalizedFromAudience = NormalizeAudienceLikeSignal(audienceEstimate.Value);
            weightedValue += normalizedFromAudience * 0.55;
            weightTotal += 0.55;
        }

        if (powerScore.HasValue)
        {
            var normalizedFromPowerScore = Math.Clamp(powerScore.Value / 100.0, 0.0, 1.0);
            weightedValue += normalizedFromPowerScore * 0.20;
            weightTotal += 0.20;
        }

        if (episodeAudienceMidpoint.HasValue && episodeAudienceMidpoint.Value > 0)
        {
            var normalizedFromEpisodeAudience = NormalizeAudienceLikeSignal(episodeAudienceMidpoint.Value);
            weightedValue += normalizedFromEpisodeAudience * 0.20;
            weightTotal += 0.20;
        }

        if (socialFollowerCount.HasValue && socialFollowerCount.Value > 0)
        {
            var normalizedFromSocialFollowers = Math.Clamp(Math.Log10(socialFollowerCount.Value + 1) / 7.0, 0.0, 1.0);
            weightedValue += normalizedFromSocialFollowers * 0.05;
            weightTotal += 0.05;
        }

        if (weightTotal > 0)
        {
            var blendedReach = Math.Clamp(weightedValue / weightTotal, 0.0, 1.0);
            if (TryNormalizeLegacyReach(audienceEstimate, powerScore, out var legacyReach))
            {
                var delta = Math.Abs(blendedReach - legacyReach);
                if (delta > 0.30)
                {
                    // Guardrail against sharp shifts if extended metrics are noisy or sparse.
                    blendedReach = Math.Clamp((blendedReach * 0.65) + (legacyReach * 0.35), 0.0, 1.0);
                }
            }

            return blendedReach;
        }

        return 0.2;
    }

    private static bool TryNormalizeLegacyReach(double? audienceEstimate, double? powerScore, out double value)
    {
        if (audienceEstimate.HasValue && audienceEstimate.Value > 0)
        {
            value = NormalizeAudienceLikeSignal(audienceEstimate.Value);
            return true;
        }

        if (powerScore.HasValue)
        {
            value = Math.Clamp(powerScore.Value / 100.0, 0.0, 1.0);
            return true;
        }

        value = 0;
        return false;
    }

    private static double NormalizeAudienceLikeSignal(double value)
    {
        return Math.Clamp(Math.Log10(value + 1) / 7.0, 0.0, 1.0);
    }

    private static bool TryGetRangeMidpoint(JsonElement node, string propertyName, out double value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(node, propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var hasFrom = TryGetDouble(property, "from", out var from);
        var hasTo = TryGetDouble(property, "to", out var to);
        if (!hasFrom && !hasTo)
        {
            return false;
        }

        if (hasFrom && hasTo)
        {
            value = (from + to) / 2.0;
            return true;
        }

        value = hasFrom ? from : to;
        return true;
    }

    private static bool TryGetFollowerCount(JsonElement node, out double value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(node, "socialFollowerCounts", out var followerCountsNode) ||
            followerCountsNode.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var total = 0.0;
        var hasAnyValue = false;
        foreach (var property in followerCountsNode.EnumerateObject())
        {
            if (!TryGetDouble(followerCountsNode, property.Name, out var followerCount) || followerCount <= 0)
            {
                continue;
            }

            total += followerCount;
            hasAnyValue = true;
        }

        if (!hasAnyValue)
        {
            return false;
        }

        value = total;
        return true;
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

    private static void WriteDebugLog(string hypothesisId, string location, string message, object data, string runId)
    {
        try
        {
            var entry = new
            {
                sessionId = "8d2ec3",
                runId,
                hypothesisId,
                location,
                message,
                data,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            File.AppendAllText("debug-8d2ec3.log", JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch
        {
            // Debug logging should never break client execution.
        }
    }

    private static void WriteDebugLogB9(string hypothesisId, string location, string message, object data, string runId)
    {
        try
        {
            var entry = new
            {
                sessionId = "b9cf3d",
                runId,
                hypothesisId,
                location,
                message,
                data,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            File.AppendAllText("debug-b9cf3d.log", JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
        catch
        {
            // Debug logging should never break client execution.
        }
    }
}
