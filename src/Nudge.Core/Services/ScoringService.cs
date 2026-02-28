using System.Text.RegularExpressions;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Core.Services;

public sealed partial class ScoringService(TimeProvider timeProvider) : IScoringService
{
    private const double ReachWeight = 0.35;
    private const double FrequencyWeight = 0.25;
    private const double NicheFitWeight = 0.40;
    private const double HighIntentWeight = 3.0;
    private const double BaselineIntentWeight = 1.0;
    private const double PenaltyIntentWeight = -2.0;
    private const double ActivityRecent30Days = 1.0;
    private const double ActivityRecent90Days = 0.7;
    private const double ActivityRecent180Days = 0.4;
    private const double ActivityStale = 0.15;
    private const int RecentEpisodeTitleWindow = 5;
    private static readonly string[] HighIntentTokens =
    [
        "athlete", "masters", "hyrox", "crossfit", "performance", "strength", "vo2", "pr", "training", "competition", "ranking"
    ];
    private static readonly string[] BaselineTokens = ["longevity", "fitness", "aging", "healthspan"];
    private static readonly string[] PenaltyTokens = ["revenue", "marketing", "entrepreneur", "coaching"];
    private static readonly string[] BusinessContextTokens = ["revenue", "marketing", "entrepreneur", "coaching", "business", "sales", "monetize", "clients"];
    private readonly TimeProvider _timeProvider = timeProvider;

    public IntentScore Score(Show show, IReadOnlyList<string> keywords)
    {
        _ = keywords;
        var reach = CalculateReach(show);
        var frequency = CalculateFrequency(show.Episodes);
        var nicheFitResult = CalculateNicheFit(show);
        var newest = show.Episodes
            .Where(e => e.PublishedAtUtc.HasValue)
            .OrderByDescending(e => e.PublishedAtUtc)
            .FirstOrDefault()
            ?.PublishedAtUtc;
        var activityScore = CalculateActivityScore(newest, _timeProvider.GetUtcNow());
        var baseScore = (reach * ReachWeight) + (frequency * FrequencyWeight) + (nicheFitResult.NormalizedScore * NicheFitWeight);
        var score = baseScore * activityScore;

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

        return Clamp01((seededReach * 0.8) + (activityQuality * 0.2));
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

    private static NicheFitBreakdown CalculateNicheFit(Show show)
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

        var hasBusinessContext = BusinessContextTokens.Any(token => tokenBag.ContainsKey(token));
        var hits = new List<NicheFitTokenHit>();
        var weightedScore = 0.0;
        var totalMatchedTokens = 0;

        ApplyTokenHits(HighIntentTokens, HighIntentWeight, tokenBag, hits, ref weightedScore, ref totalMatchedTokens);
        ApplyTokenHits(BaselineTokens, BaselineIntentWeight, tokenBag, hits, ref weightedScore, ref totalMatchedTokens);
        ApplyTokenHits(PenaltyTokens, PenaltyIntentWeight, tokenBag, hits, ref weightedScore, ref totalMatchedTokens);
        if (hasBusinessContext)
        {
            ApplyTokenHits(["wellness"], PenaltyIntentWeight, tokenBag, hits, ref weightedScore, ref totalMatchedTokens);
        }

        var positiveContribution = hits
            .Where(hit => hit.Contribution > 0)
            .Sum(hit => hit.Contribution);
        var penaltyMagnitude = hits
            .Where(hit => hit.Contribution < 0)
            .Sum(hit => -hit.Contribution);
        var normalizedScore = positiveContribution <= 0
            ? 0
            : Clamp01(positiveContribution / (positiveContribution + penaltyMagnitude + 1.0));

        return new NicheFitBreakdown
        {
            TokenHits = hits,
            WeightedScore = weightedScore,
            NormalizedScore = normalizedScore,
            PositiveContribution = positiveContribution,
            PenaltyMagnitude = penaltyMagnitude,
            TotalMatchedTokens = totalMatchedTokens,
            BusinessContextDetected = hasBusinessContext
        };
    }

    private static Dictionary<string, int> BuildNicheTokenBag(Show show)
    {
        var recentTitles = show.Episodes
            .OrderByDescending(e => e.PublishedAtUtc ?? DateTimeOffset.MinValue)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .Take(RecentEpisodeTitleWindow)
            .Select(e => e.Title);
        var corpus = string.Join(' ', [show.Name, show.Description ?? string.Empty, string.Join(' ', recentTitles)]);
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

    private static double CalculateActivityScore(DateTimeOffset? newestEpisodePublishedAtUtc, DateTimeOffset now)
    {
        if (!newestEpisodePublishedAtUtc.HasValue)
        {
            return ActivityStale;
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

        return ActivityStale;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    [GeneratedRegex("[a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex WordTokenRegex();
}
