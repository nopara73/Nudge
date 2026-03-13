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
    private const int RecentTitleTokenMultiplier = 2;
    private const double ActivityRecent30Days = 1.0;
    private const double ActivityRecent90Days = 0.7;
    private const double ActivityRecent180Days = 0.4;
    private const double ActivityStaleDense = 0.12;
    private const double ActivityStaleSparse = 0.06;
    private const double ActivityStaleSingleEpisode = 0.03;
    private const int RecentEpisodeTitleWindow = 5;
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
        var keywordTokenFrequencies = BuildKeywordTokenFrequency(keywords);
        if (tokenBag.Count == 0 || keywordTokenFrequencies.Count == 0)
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

        var normalizedCorpus = NormalizeKeywordPhrase(BuildNicheCorpus(show));
        var hitMap = new Dictionary<string, NicheFitTokenHit>(StringComparer.OrdinalIgnoreCase);
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

            var keywordWeightTotal = 0.0;
            foreach (var token in keywordTokens)
            {
                var tokenWeight = GetKeywordTokenWeight(token, keywordTokenFrequencies);
                if (tokenWeight <= 0)
                {
                    continue;
                }

                keywordWeightTotal += tokenWeight;
            }

            if (keywordWeightTotal <= 0)
            {
                perKeywordScores.Add(0);
                continue;
            }

            if (keywordTokens.Length > 1)
            {
                var normalizedPhrase = NormalizeKeywordPhrase(rawKeyword);
                var phraseMatched = !string.IsNullOrWhiteSpace(normalizedPhrase) &&
                                    normalizedCorpus.Contains(normalizedPhrase, StringComparison.Ordinal);
                if (phraseMatched)
                {
                    hitMap[normalizedPhrase] = new NicheFitTokenHit
                    {
                        Token = normalizedPhrase,
                        Hits = 1,
                        Weight = keywordWeightTotal,
                        Contribution = keywordWeightTotal
                    };
                }

                perKeywordScores.Add(phraseMatched ? 1.0 : 0.0);
                continue;
            }

            var keywordMatchedWeight = 0.0;
            foreach (var token in keywordTokens)
            {
                var tokenWeight = GetKeywordTokenWeight(token, keywordTokenFrequencies);
                if (tokenWeight <= 0)
                {
                    continue;
                }

                if (!tokenBag.TryGetValue(token, out var hitCount) || hitCount <= 0)
                {
                    continue;
                }

                keywordMatchedWeight += tokenWeight;
                if (hitMap.TryGetValue(token, out var existingHit))
                {
                    hitMap[token] = existingHit with
                    {
                        Hits = existingHit.Hits + hitCount,
                        Contribution = existingHit.Contribution + (hitCount * tokenWeight)
                    };
                }
                else
                {
                    hitMap[token] = new NicheFitTokenHit
                    {
                        Token = token,
                        Hits = hitCount,
                        Weight = tokenWeight,
                        Contribution = hitCount * tokenWeight
                    };
                }
            }

            var keywordScore = Clamp01(keywordMatchedWeight / keywordWeightTotal);
            perKeywordScores.Add(keywordScore);
        }

        var hits = hitMap.Values
            .OrderByDescending(hit => hit.Contribution)
            .ThenBy(hit => hit.Token, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var positiveContribution = hits.Sum(hit => hit.Contribution);
        var normalizedScore = perKeywordScores.Count == 0 ? 0 : Clamp01(perKeywordScores.Average());
        var totalMatchedTokens = hits.Sum(hit => hit.Hits);
        // #region agent log
        WriteDebugLogB9(
            hypothesisId: "H2_query_driven_niche_fit",
            location: "ScoringService.cs:CalculateNicheFit",
            message: "Calculated niche fit from user-supplied keywords.",
            data: new
            {
                showId = show.Id,
                showName = show.Name,
                totalMatchedTokens,
                weightedScore = positiveContribution,
                positiveContribution,
                perKeywordScoresCount = perKeywordScores.Count,
                averageKeywordScore = normalizedScore,
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
            WeightedScore = positiveContribution,
            NormalizedScore = normalizedScore,
            PositiveContribution = positiveContribution,
            PenaltyMagnitude = 0,
            TotalMatchedTokens = totalMatchedTokens,
            BusinessContextDetected = false
        };
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

    private static double GetKeywordTokenWeight(string token, IReadOnlyDictionary<string, int> keywordTokenFrequencies)
    {
        if (!keywordTokenFrequencies.TryGetValue(token, out var frequency) || frequency <= 0)
        {
            return 0;
        }

        return 1.0 / frequency;
    }

    private static string NormalizeKeywordPhrase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            WordTokenRegex()
                .Matches(value.ToLowerInvariant())
                .Select(match => match.Value));
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
