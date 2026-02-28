using Nudge.Cli.Models;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class PodcastRankingPipeline(
    IPodcastSearchClient searchClient,
    IRssFeedClient feedClient,
    IRssParser rssParser,
    IScoringService scoringService,
    TimeProvider timeProvider)
{
    private const double MissingEmailPenalty = 0.03;
    private static readonly HashSet<string> EnglishSignalWords = new(StringComparer.Ordinal)
    {
        "about", "and", "episode", "from", "health", "interview", "science", "the", "this", "with"
    };
    private static readonly HashSet<string> HungarianSignalWords = new(StringComparer.Ordinal)
    {
        "beszelgetes", "egy", "es", "hogy", "interju", "magyar", "mert", "nem", "vagy", "van"
    };
    private static readonly char[] HungarianDiacritics = ['á', 'é', 'í', 'ó', 'ö', 'ő', 'ú', 'ü', 'ű'];
    private static readonly char[] TokenSeparators =
    [
        ' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'',
        '/', '\\', '|', '-', '_', '+', '=', '*', '&', '#', '@', '%', '^', '$', '<', '>', '~', '`'
    ];
    private readonly IPodcastSearchClient _searchClient = searchClient;
    private readonly IRssFeedClient _feedClient = feedClient;
    private readonly IRssParser _rssParser = rssParser;
    private readonly IScoringService _scoringService = scoringService;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<RankingRunResult> RunAsync(
        CliArguments arguments,
        bool includeDebugDiagnostics = false,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var diagnostics = new List<string>();
        var candidates = await _searchClient.SearchAsync(arguments.Keywords, arguments.PublishedAfterDays, cancellationToken);
        if (includeDebugDiagnostics)
        {
            diagnostics.Add($"Raw API shows before local filtering: {candidates.Count}");
        }
        var thresholdUtc = _timeProvider.GetUtcNow().AddDays(-arguments.PublishedAfterDays);
        var ranked = await BuildRankedTargetsAsync(candidates, arguments.Keywords, thresholdUtc, applyRecencyFilter: true, warnings, cancellationToken);
        if (ranked.Count == 0)
        {
            if (includeDebugDiagnostics)
            {
                diagnostics.Add("No ranked results after local recency filtering; retrying without recency filter.");
            }
            ranked = await BuildRankedTargetsAsync(candidates, arguments.Keywords, thresholdUtc, applyRecencyFilter: false, warnings, cancellationToken);
        }

        var ordered = ranked
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.NicheFit)
            .ThenByDescending(r => r.NewestEpisodePublishedAtUtc)
            .ThenBy(r => r.ShowName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ShowId, StringComparer.Ordinal)
            .ToArray();

        return new RankingRunResult
        {
            Results = ordered,
            Warnings = warnings
                .Distinct(StringComparer.Ordinal)
                .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Diagnostics = diagnostics
        };
    }

    private async Task<List<RankedTarget>> BuildRankedTargetsAsync(
        IReadOnlyList<PodcastSearchResult> candidates,
        IReadOnlyList<string> keywords,
        DateTimeOffset thresholdUtc,
        bool applyRecencyFilter,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var ranked = new List<RankedTarget>();
        var missingContactShows = new List<string>();
        using var semaphore = new SemaphoreSlim(5, 5);
        var tasks = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var rankedTarget = await BuildRankedTargetAsync(candidate, keywords, thresholdUtc, applyRecencyFilter, missingContactShows, cancellationToken);
                if (rankedTarget is not null)
                {
                    lock (ranked)
                    {
                        ranked.Add(rankedTarget);
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lock (warnings)
                {
                    warnings.Add($"Skipped '{candidate.Name}' feed after retry ({DescribeFailure(ex)}).");
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        if (missingContactShows.Count > 0)
        {
            var sampleShows = missingContactShows
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();
            var sample = string.Join(", ", sampleShows.Select(name => $"'{name}'"));
            var remainder = Math.Max(0, missingContactShows.Distinct(StringComparer.OrdinalIgnoreCase).Count() - sampleShows.Length);
            var suffix = remainder > 0 ? $" (+{remainder} more)." : ".";
            warnings.Add($"Missing contact email penalty applied to {missingContactShows.Distinct(StringComparer.OrdinalIgnoreCase).Count()} show(s): {sample}{suffix}");
        }

        return ranked;
    }

    private async Task<RankedTarget?> BuildRankedTargetAsync(
        PodcastSearchResult candidate,
        IReadOnlyList<string> keywords,
        DateTimeOffset thresholdUtc,
        bool applyRecencyFilter,
        List<string> missingContactShows,
        CancellationToken cancellationToken)
    {
        var xml = await _feedClient.GetFeedXmlAsync(candidate.FeedUrl, cancellationToken);
        var parseResult = await _rssParser.ParseAsync(xml, cancellationToken);
        if (!parseResult.Success || parseResult.Payload is null)
        {
            return null;
        }

        var allEpisodes = parseResult.Payload.Episodes.ToArray();
        if (!TryDetectAllowedLanguage(candidate, out var detectedLanguage))
        {
            return null;
        }

        var eligibleEpisodes = applyRecencyFilter
            ? allEpisodes
                .Where(e => e.PublishedAtUtc.HasValue && e.PublishedAtUtc.Value >= thresholdUtc)
                .ToArray()
            : allEpisodes;

        if (applyRecencyFilter && eligibleEpisodes.Length == 0)
        {
            // Keep stale feeds in play so recency can be penalized by scoring instead of hard-filtering out.
            eligibleEpisodes = allEpisodes;
        }

        if (eligibleEpisodes.Length == 0)
        {
            return null;
        }

        var missingContactEmail = string.IsNullOrWhiteSpace(parseResult.Payload.PodcastEmail);
        if (missingContactEmail)
        {
            lock (missingContactShows)
            {
                missingContactShows.Add(candidate.Name);
            }
        }

        var show = new Show
        {
            Id = candidate.Id,
            Name = candidate.Name,
            Description = candidate.Description,
            FeedUrl = candidate.FeedUrl,
            EstimatedReach = candidate.EstimatedReach,
            ContactMethod = missingContactEmail ? ContactMethod.None : ContactMethod.Email,
            ContactValue = parseResult.Payload.PodcastEmail,
            Episodes = eligibleEpisodes
        };

        var intent = _scoringService.Score(show, keywords);
        var adjustedScore = missingContactEmail ? Math.Max(0, intent.Score - MissingEmailPenalty) : intent.Score;
        var outreachPriority = ClassifyOutreachPriority(
            score: adjustedScore,
            activityScore: intent.ActivityScore,
            frequency: intent.Frequency,
            nicheFit: intent.NicheFit,
            hasContactEmail: !missingContactEmail);
        return new RankedTarget
        {
            ShowId = show.Id,
            ShowName = show.Name,
            DetectedLanguage = detectedLanguage,
            FeedUrl = show.FeedUrl,
            ContactEmail = show.ContactValue,
            Reach = intent.Reach,
            Frequency = intent.Frequency,
            NicheFit = intent.NicheFit,
            ActivityScore = intent.ActivityScore,
            NicheFitBreakdown = intent.NicheFitBreakdown,
            OutreachPriority = outreachPriority,
            Score = adjustedScore,
            NewestEpisodePublishedAtUtc = intent.NewestEpisodePublishedAtUtc,
            RecentEpisodeTitles = eligibleEpisodes.Select(e => e.Title).ToArray()
        };
    }

    private static string ClassifyOutreachPriority(
        double score,
        double activityScore,
        double frequency,
        double nicheFit,
        bool hasContactEmail)
    {
        var isHighSignal = score >= 0.55 && activityScore >= 0.7 && frequency >= 0.55 && nicheFit >= 0.55;
        if (isHighSignal && hasContactEmail)
        {
            return "High";
        }

        var isMediumSignal = score >= 0.30 && activityScore >= 0.4 && nicheFit >= 0.45;
        if (isMediumSignal)
        {
            return "Medium";
        }

        return "Low";
    }

    private static string DescribeFailure(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: { } statusCode })
        {
            return $"HTTP {(int)statusCode}";
        }

        if (ex is HttpRequestException)
        {
            return "HTTP request failed";
        }

        return "feed fetch failed";
    }

    private static bool TryDetectAllowedLanguage(PodcastSearchResult candidate, out string detectedLanguage)
    {
        detectedLanguage = string.Empty;
        if (TryClassifyLanguageTag(candidate.Language, out var tagClassification))
        {
            if (tagClassification is LanguageClassification.English or LanguageClassification.Hungarian)
            {
                detectedLanguage = ToLanguageCode(tagClassification);
                return true;
            }

            return false;
        }

        var inferred = InferLanguageFromText(candidate);
        if (inferred is LanguageClassification.English or LanguageClassification.Hungarian)
        {
            detectedLanguage = ToLanguageCode(inferred);
            return true;
        }

        return false;
    }

    private static bool TryClassifyLanguageTag(string? rawLanguage, out LanguageClassification classification)
    {
        classification = LanguageClassification.Unknown;
        if (string.IsNullOrWhiteSpace(rawLanguage))
        {
            return false;
        }

        var normalized = rawLanguage.Trim().ToLowerInvariant().Replace('_', '-');
        if (normalized.StartsWith("en", StringComparison.Ordinal) || normalized.Contains("english", StringComparison.Ordinal))
        {
            classification = LanguageClassification.English;
            return true;
        }

        if (normalized.StartsWith("hu", StringComparison.Ordinal) || normalized.Contains("hungarian", StringComparison.Ordinal))
        {
            classification = LanguageClassification.Hungarian;
            return true;
        }

        classification = LanguageClassification.Other;
        return true;
    }

    private static LanguageClassification InferLanguageFromText(PodcastSearchResult candidate)
    {
        var text = $"{candidate.Name} {candidate.Description}";
        var normalized = text.ToLowerInvariant();

        if (normalized.IndexOfAny(HungarianDiacritics) >= 0)
        {
            return LanguageClassification.Hungarian;
        }

        var englishHits = 0;
        var hungarianHits = 0;
        foreach (var token in normalized.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (EnglishSignalWords.Contains(token))
            {
                englishHits++;
            }

            if (HungarianSignalWords.Contains(token))
            {
                hungarianHits++;
            }
        }

        if (hungarianHits >= 2 && hungarianHits >= englishHits + 1)
        {
            return LanguageClassification.Hungarian;
        }

        if (englishHits >= 2 && englishHits >= hungarianHits)
        {
            return LanguageClassification.English;
        }

        return LanguageClassification.Unknown;
    }

    private static string ToLanguageCode(LanguageClassification classification)
    {
        return classification switch
        {
            LanguageClassification.English => "en",
            LanguageClassification.Hungarian => "hu",
            _ => string.Empty
        };
    }

    private enum LanguageClassification
    {
        Unknown,
        English,
        Hungarian,
        Other
    }
}
