using System.Text.RegularExpressions;
using System.Text.Json;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Core.Services;

public sealed partial class ScoringService(TimeProvider timeProvider) : IScoringService
{
    private const double ReachWeight = 0.35;
    private const double FrequencyWeight = 0.25;
    private const double NicheFitWeight = 0.40;
    private const double BaselineIntentWeight = 1.0;
    private const double PenaltyIntentWeight = -2.0;
    private const double SoftPenaltyIntentWeight = -0.75;
    private const int RecentTitleTokenMultiplier = 2;
    private const double ActivityRecent30Days = 1.0;
    private const double ActivityRecent90Days = 0.7;
    private const double ActivityRecent180Days = 0.4;
    private const double ActivityStaleDense = 0.12;
    private const double ActivityStaleSparse = 0.06;
    private const double ActivityStaleSingleEpisode = 0.03;
    private const int RecentEpisodeTitleWindow = 5;
    private const double KeywordAlignmentFloor = 0.25;
    private const int KeywordAlignmentTopKeywordCount = 6;
    private static readonly string[] BaselineTokens = ["longevity", "fitness", "aging", "healthspan"];
    private static readonly string[] PenaltyTokens = ["revenue", "sales", "monetize", "clients"];
    private static readonly string[] SoftPenaltyTokens =
    [
        "wellness", "mindset", "entrepreneur", "entrepreneurship", "marketing", "coaching", "business", "biohacking", "lifestyle"
    ];
    private static readonly HashSet<string> GenericKeywordTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "athlete", "athletes", "fitness", "longevity", "aging", "health", "healthspan", "performance", "training",
        "strength", "masters", "competition"
    };
    private readonly TimeProvider _timeProvider = timeProvider;

    public IntentScore Score(Show show, IReadOnlyList<string> keywords)
    {
        var reach = CalculateReach(show);
        var frequency = CalculateFrequency(show.Episodes);
        var nicheFitResult = CalculateNicheFit(show, keywords);
        var newest = show.Episodes
            .Where(e => e.PublishedAtUtc.HasValue)
            .OrderByDescending(e => e.PublishedAtUtc)
            .FirstOrDefault()
            ?.PublishedAtUtc;
        var activityScore = CalculateActivityScore(newest, show.Episodes.Count, _timeProvider.GetUtcNow());
        var baseScore = (reach * ReachWeight) + (frequency * FrequencyWeight) + (nicheFitResult.NormalizedScore * NicheFitWeight);
        var score = baseScore * activityScore;
        // #region agent log
        WriteDebugLogB9(
            hypothesisId: "H5_activity_not_filtering_relevance",
            location: "ScoringService.cs:Score",
            message: "Computed final score components.",
            data: new
            {
                showId = show.Id,
                showName = show.Name,
                episodeCount = show.Episodes.Count,
                newestEpisodePublishedAtUtc = newest,
                reach,
                frequency,
                nicheFit = nicheFitResult.NormalizedScore,
                baseScore,
                activityScore,
                finalScore = score
            },
            runId: "initial");
        // #endregion

        return new IntentScore
        {
            ShowId = show.Id,
            ShowName = show.Name,
            Reach = reach,
            Frequency = frequency,
            NicheFit = nicheFitResult.NormalizedScore,
            ActivityScore = activityScore,
            Score = score,
            NicheFitBreakdown = nicheFitResult,
            NewestEpisodePublishedAtUtc = newest,
            ContactEmail = show.ContactMethod == ContactMethod.Email ? show.ContactValue : null
        };
    }

    private double CalculateReach(Show show)
    {
        var seededReach = Clamp01(show.EstimatedReach);
        var episodes = show.Episodes;
        if (episodes.Count == 0)
        {
            return seededReach;
        }

        var validDateCount = episodes.Count(e => e.PublishedAtUtc.HasValue);
        var validDateRatio = (double)validDateCount / episodes.Count;
        var episodeWindowScore = Clamp01(episodes.Count / 3.0);
        var now = _timeProvider.GetUtcNow();
        var hasRecent = episodes.Any(e => e.PublishedAtUtc.HasValue && (now - e.PublishedAtUtc.Value).TotalDays <= 30)
            ? 1.0
            : 0.0;
        var activityQuality = Clamp01((episodeWindowScore * 0.5) + (validDateRatio * 0.3) + (hasRecent * 0.2));

        // Reach is an audience-size proxy and should not be inflated by activity.
        // Apply a mild confidence discount for poor episode metadata instead.
        var confidenceMultiplier = 0.8 + (activityQuality * 0.2);
        return Clamp01(seededReach * confidenceMultiplier);
    }

    private double CalculateFrequency(IReadOnlyList<Episode> episodes)
    {
        var datedEpisodes = episodes
            .Where(e => e.PublishedAtUtc.HasValue)
            .OrderByDescending(e => e.PublishedAtUtc)
            .Select(e => e.PublishedAtUtc!.Value)
            .Take(3)
            .ToArray();

        if (datedEpisodes.Length == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var latestAgeDays = (now - datedEpisodes[0]).TotalDays;
        var recencyScore = Clamp01(1 - (latestAgeDays / 60.0));

        if (datedEpisodes.Length == 1)
        {
            return recencyScore;
        }

        var gaps = new List<double>(datedEpisodes.Length - 1);
        for (var i = 0; i < datedEpisodes.Length - 1; i++)
        {
            var gap = (datedEpisodes[i] - datedEpisodes[i + 1]).TotalDays;
            if (gap >= 0)
            {
                gaps.Add(gap);
            }
        }

        var cadenceScore = gaps.Count == 0
            ? 0
            : AverageGapToCadenceScore(gaps.Average());

        return Clamp01((recencyScore * 0.6) + (cadenceScore * 0.4));
    }

    private static double AverageGapToCadenceScore(double averageGapDays)
    {
        if (averageGapDays <= 7)
        {
            return 1;
        }

        if (averageGapDays >= 45)
        {
            return 0;
        }

        return 1 - ((averageGapDays - 7) / (45 - 7));
    }

    private static NicheFitBreakdown CalculateNicheFit(Show show, IReadOnlyList<string> keywords)
    {
        var tokenBag = BuildNicheTokenBag(show);
        if (tokenBag.Count == 0)
        {
            return new NicheFitBreakdown
            {
                TokenHits = [],
                WeightedScore = 0,
                NormalizedScore = 0,
                PositiveContribution = 0,
                PenaltyMagnitude = 0,
                TotalMatchedTokens = 0,
                BusinessContextDetected = false
            };
        }

        var hits = new List<NicheFitTokenHit>();
        var weightedScore = 0.0;
        var totalMatchedTokens = 0;

        ApplyTokenHits(BaselineTokens, BaselineIntentWeight, tokenBag, hits, ref weightedScore, ref totalMatchedTokens);
        ApplyTokenHits(PenaltyTokens, PenaltyIntentWeight, tokenBag, hits, ref weightedScore, ref totalMatchedTokens);
        ApplyTokenHits(SoftPenaltyTokens, SoftPenaltyIntentWeight, tokenBag, hits, ref weightedScore, ref totalMatchedTokens);

        var positiveContribution = hits
            .Where(hit => hit.Contribution > 0)
            .Sum(hit => hit.Contribution);
        var keywordAlignment = CalculateKeywordAlignment(show, tokenBag, keywords);
        var alignedPositiveContribution = keywordAlignment <= 0
            ? 0
            : positiveContribution * (KeywordAlignmentFloor + ((1 - KeywordAlignmentFloor) * keywordAlignment));
        var penaltyMagnitude = hits
            .Where(hit => hit.Contribution < 0)
            .Sum(hit => -hit.Contribution);
        var normalizedScore = alignedPositiveContribution <= 0
            ? 0
            : Clamp01(alignedPositiveContribution / (alignedPositiveContribution + penaltyMagnitude + 1.0));
        // #region agent log
        WriteDebugLogB9(
            hypothesisId: "H2_H3_generic_token_overweight",
            location: "ScoringService.cs:CalculateNicheFit",
            message: "Calculated niche fit contributions and alignment.",
            data: new
            {
                showId = show.Id,
                showName = show.Name,
                totalMatchedTokens,
                weightedScore,
                positiveContribution,
                keywordAlignment,
                alignedPositiveContribution,
                penaltyMagnitude,
                normalizedScore,
                topHits = hits
                    .OrderByDescending(h => Math.Abs(h.Contribution))
                    .Take(8)
                    .Select(h => new { h.Token, h.Hits, h.Weight, h.Contribution })
                    .ToArray()
            },
            runId: "initial");
        // #endregion

        return new NicheFitBreakdown
        {
            TokenHits = hits,
            WeightedScore = weightedScore,
            NormalizedScore = normalizedScore,
            PositiveContribution = alignedPositiveContribution,
            PenaltyMagnitude = penaltyMagnitude,
            TotalMatchedTokens = totalMatchedTokens,
            BusinessContextDetected = PenaltyTokens.Any(token => tokenBag.ContainsKey(token))
        };
    }

    private static double CalculateKeywordAlignment(Show show, IReadOnlyDictionary<string, int> tokenBag, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return 1;
        }

        var keywordTokenFrequencies = BuildKeywordTokenFrequency(keywords);
        if (keywordTokenFrequencies.Count == 0)
        {
            return 1;
        }

        var maxTokenSpecificity = keywordTokenFrequencies
            .Keys
            .Select(token => GetKeywordTokenSpecificity(token, keywordTokenFrequencies))
            .DefaultIfEmpty(1.0)
            .Max();
        var corpus = BuildNicheCorpus(show).ToLowerInvariant();
        var perKeywordScores = new List<double>(keywords.Count);
        foreach (var rawKeyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(rawKeyword))
            {
                continue;
            }

            var normalizedKeyword = rawKeyword.Trim().ToLowerInvariant();
            var keywordTokens = WordTokenRegex()
                .Matches(normalizedKeyword)
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (keywordTokens.Length == 0)
            {
                continue;
            }

            var singleTokenSpecificityScore = keywordTokens.Length == 1 && tokenBag.ContainsKey(keywordTokens[0])
                ? Clamp01(GetKeywordTokenSpecificity(keywordTokens[0], keywordTokenFrequencies) / maxTokenSpecificity)
                : 0.0;
            var keywordScore = keywordTokens.Length == 1
                ? singleTokenSpecificityScore
                : (corpus.Contains(normalizedKeyword, StringComparison.Ordinal) ? 1.0 : 0.0);
            perKeywordScores.Add(keywordScore);
        }

        if (perKeywordScores.Count == 0)
        {
            return 0;
        }

        var topCount = Math.Min(KeywordAlignmentTopKeywordCount, perKeywordScores.Count);
        var alignment = perKeywordScores
            .OrderByDescending(score => score)
            .Take(topCount)
            .Average();
        // #region agent log
        WriteDebugLogB9(
            hypothesisId: "H3_keyword_alignment_too_permissive",
            location: "ScoringService.cs:CalculateKeywordAlignment",
            message: "Calculated per-keyword alignment for show.",
            data: new
            {
                showId = show.Id,
                showName = show.Name,
                keywordCount = keywords.Count,
                perKeywordScoresCount = perKeywordScores.Count,
                topCount,
                topScores = perKeywordScores.OrderByDescending(s => s).Take(topCount).ToArray(),
                alignment
            },
            runId: "initial");
        // #endregion
        return alignment;
    }

    private static Dictionary<string, int> BuildKeywordTokenFrequency(IReadOnlyList<string> keywords)
    {
        var frequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            var distinctKeywordTokens = WordTokenRegex()
                .Matches(keyword.Trim().ToLowerInvariant())
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var token in distinctKeywordTokens)
            {
                if (frequencies.TryGetValue(token, out var count))
                {
                    frequencies[token] = count + 1;
                }
                else
                {
                    frequencies[token] = 1;
                }
            }
        }

        return frequencies;
    }

    private static double GetKeywordTokenSpecificity(string token, IReadOnlyDictionary<string, int> keywordTokenFrequencies)
    {
        if (!keywordTokenFrequencies.TryGetValue(token, out var frequency) || frequency <= 0)
        {
            return 0;
        }

        var baseSpecificity = 1.0 / frequency;
        return GenericKeywordTokens.Contains(token)
            ? baseSpecificity * 0.45
            : baseSpecificity;
    }

    private static Dictionary<string, int> BuildNicheTokenBag(Show show)
    {
        var corpus = BuildNicheCorpus(show);
        if (string.IsNullOrWhiteSpace(corpus))
        {
            return [];
        }

        var tokenBag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in WordTokenRegex().Matches(corpus.ToLowerInvariant()))
        {
            var token = match.Value;
            if (tokenBag.TryGetValue(token, out var count))
            {
                tokenBag[token] = count + 1;
            }
            else
            {
                tokenBag[token] = 1;
            }
        }

        return tokenBag;
    }

    private static string BuildNicheCorpus(Show show)
    {
        var recentTitles = show.Episodes
            .OrderByDescending(e => e.PublishedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Take(RecentEpisodeTitleWindow)
            .Select(e => e.Title)
            .ToArray();
        var weightedRecentTitles = string.Join(' ', Enumerable.Repeat(string.Join(' ', recentTitles), RecentTitleTokenMultiplier));
        return string.Join(' ', [show.Name, show.Description ?? string.Empty, weightedRecentTitles]);
    }

    private static void ApplyTokenHits(
        IReadOnlyList<string> tokens,
        double weight,
        IReadOnlyDictionary<string, int> tokenBag,
        List<NicheFitTokenHit> hits,
        ref double weightedScore,
        ref int totalMatchedTokens)
    {
        foreach (var token in tokens)
        {
            if (!tokenBag.TryGetValue(token, out var count) || count <= 0)
            {
                continue;
            }

            var contribution = count * weight;
            weightedScore += contribution;
            totalMatchedTokens += count;
            hits.Add(new NicheFitTokenHit
            {
                Token = token,
                Hits = count,
                Weight = weight,
                Contribution = contribution
            });
        }
    }

    private static double CalculateActivityScore(DateTimeOffset? newestEpisodePublishedAtUtc, int episodeCount, DateTimeOffset now)
    {
        if (!newestEpisodePublishedAtUtc.HasValue)
        {
            return episodeCount <= 1
                ? ActivityStaleSingleEpisode
                : ActivityStaleSparse;
        }

        var ageDays = (now - newestEpisodePublishedAtUtc.Value).TotalDays;
        if (ageDays <= 30)
        {
            return ActivityRecent30Days;
        }

        if (ageDays <= 90)
        {
            return ActivityRecent90Days;
        }

        if (ageDays <= 180)
        {
            return ActivityRecent180Days;
        }

        if (episodeCount <= 1)
        {
            return ActivityStaleSingleEpisode;
        }

        if (episodeCount <= 3)
        {
            return ActivityStaleSparse;
        }

        return ActivityStaleDense;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    [GeneratedRegex("[a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex WordTokenRegex();

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
            // Debug logging must never impact scoring behavior.
        }
    }
}
