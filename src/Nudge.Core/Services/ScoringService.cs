using System.Text.RegularExpressions;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Core.Services;

public sealed partial class ScoringService(TimeProvider timeProvider) : IScoringService
{
    private const double ReachWeight = 0.35;
    private const double FrequencyWeight = 0.25;
    private const double NicheFitWeight = 0.40;
    private readonly TimeProvider _timeProvider = timeProvider;

    public IntentScore Score(Show show, IReadOnlyList<string> keywords)
    {
        var reach = CalculateReach(show);
        var frequency = CalculateFrequency(show.Episodes);
        var nicheFit = CalculateNicheFit(show, keywords);
        var score = Clamp01((reach * ReachWeight) + (frequency * FrequencyWeight) + (nicheFit * NicheFitWeight));
        var newest = show.Episodes
            .Where(e => e.PublishedAtUtc.HasValue)
            .OrderByDescending(e => e.PublishedAtUtc)
            .FirstOrDefault()
            ?.PublishedAtUtc;

        return new IntentScore
        {
            ShowId = show.Id,
            ShowName = show.Name,
            Reach = reach,
            Frequency = frequency,
            NicheFit = nicheFit,
            Score = score,
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

    private static double CalculateNicheFit(Show show, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return 0;
        }

        var normalizedKeywords = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (normalizedKeywords.Length == 0)
        {
            return 0;
        }

        var showTitleScore = MatchRatio(show.Name, normalizedKeywords);
        var episodeTitleCorpus = string.Join(' ', show.Episodes.Select(e => e.Title));
        var episodeDescCorpus = string.Join(' ', show.Episodes.Select(e => e.Description));
        var episodeTitleScore = MatchRatio(episodeTitleCorpus, normalizedKeywords);
        var episodeDescScore = MatchRatio(episodeDescCorpus, normalizedKeywords);

        return Clamp01((showTitleScore * 0.5) + (episodeTitleScore * 0.3) + (episodeDescScore * 0.2));
    }

    private static double MatchRatio(string text, IReadOnlyCollection<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var tokenSet = WordTokenRegex()
            .Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (tokenSet.Count == 0)
        {
            return 0;
        }

        var matches = keywords.Count(tokenSet.Contains);
        return (double)matches / keywords.Count;
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    [GeneratedRegex("[a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex WordTokenRegex();
}
