using Nudge.Core.Models;
using Nudge.Core.Services;

namespace Nudge.Tests;

public sealed class ScoringServiceTests
{
    [Fact]
    public void Score_UsesWeightedFormulaAndExposesComponents()
    {
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero));
        var service = new ScoringService(timeProvider);
        var show = new Show
        {
            Id = "show-1",
            Name = "Masters Performance Training",
            Description = "Athlete-focused strength and VO2 sessions.",
            EstimatedReach = 0.7,
            Episodes =
            [
                new Episode("Competition prep", "Athlete training block", new DateTimeOffset(2026, 2, 24, 0, 0, 0, TimeSpan.Zero)),
                new Episode("Crossfit PR recap", "Strength and ranking analysis", new DateTimeOffset(2026, 2, 17, 0, 0, 0, TimeSpan.Zero)),
                new Episode("Hyrox strategy", "Masters race planning", new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero))
            ]
        };

        var result = service.Score(show, ["longevity", "fitness"]);
        var expected = (result.Reach * 0.35) + (result.Frequency * 0.25) + (result.NicheFit * 0.40);

        Assert.InRange(result.Reach, 0, 1);
        Assert.InRange(result.Frequency, 0, 1);
        Assert.Equal(expected, result.Score, 10);
        Assert.NotEmpty(result.NicheFitBreakdown.TokenHits);
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "athlete" && t.Weight == 3.0);
    }

    [Fact]
    public void Score_WithNoEpisodes_UsesSeededReachOnly()
    {
        var service = new ScoringService(new FixedTimeProvider(new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero)));
        var show = new Show
        {
            Id = "show-2",
            Name = "Sparse Feed",
            EstimatedReach = 0.5,
            Episodes = Array.Empty<Episode>()
        };

        var result = service.Score(show, ["startup"]);
        Assert.Equal(0.5, result.Reach, 10);
        Assert.Equal(0, result.Frequency, 10);
        Assert.Equal(0, result.NicheFit, 10);
        Assert.Equal(0.175, result.Score, 10);
    }

    [Fact]
    public void Score_AthleteFocusedShow_OutranksGenericLongevity_WhenCadenceIsSimilar()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var athleteShow = BuildShow(
            id: "athlete",
            name: "Athlete Longevity Performance",
            description: "Masters athlete strength and VO2 development.",
            estimatedReach: 0.65,
            now,
            "Training for competition",
            "Crossfit PR improvements",
            "Ranking and performance review");
        var genericShow = BuildShow(
            id: "generic",
            name: "Longevity Science Weekly",
            description: "Longevity and healthspan science updates.",
            estimatedReach: 0.65,
            now,
            "Aging pathways overview",
            "Healthspan biomarkers",
            "Longevity interview");

        var athleteScore = service.Score(athleteShow, ["longevity", "fitness"]);
        var genericScore = service.Score(genericShow, ["longevity", "fitness"]);

        Assert.True(athleteScore.NicheFit > genericScore.NicheFit);
        Assert.True(athleteScore.Score > genericScore.Score);
    }

    [Fact]
    public void Score_BusinessFocusedFitnessShow_ReceivesPenalty()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var show = BuildShow(
            id: "business",
            name: "Fitness Marketing Revenue",
            description: "Entrepreneur playbook for coaching business growth in wellness.",
            estimatedReach: 0.7,
            now,
            "Client acquisition marketing",
            "Coaching sales funnel",
            "Revenue playbook");

        var result = service.Score(show, ["fitness"]);

        Assert.True(result.NicheFit < 0);
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "revenue" && t.Weight == -2.0);
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "wellness" && t.Weight == -2.0);
        Assert.True(result.NicheFitBreakdown.BusinessContextDetected);
    }

    [Fact]
    public void Score_PureLongevityScience_RemainsButBelowPerformanceHeavy()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var longevityShow = BuildShow(
            id: "longevity",
            name: "Longevity Science Briefing",
            description: "Aging and healthspan biology.",
            estimatedReach: 0.7,
            now,
            "Aging pathways",
            "Longevity evidence",
            "Healthspan mechanisms");
        var performanceShow = BuildShow(
            id: "performance",
            name: "Performance Longevity Lab",
            description: "Athlete strength training and VO2 optimization.",
            estimatedReach: 0.7,
            now,
            "Competition prep",
            "PR progress review",
            "Masters training block");

        var longevityScore = service.Score(longevityShow, ["longevity"]);
        var performanceScore = service.Score(performanceShow, ["longevity"]);

        Assert.True(longevityScore.NicheFit > 0);
        Assert.True(longevityScore.NicheFit < performanceScore.NicheFit);
    }

    private static Show BuildShow(
        string id,
        string name,
        string description,
        double estimatedReach,
        DateTimeOffset now,
        string episode1,
        string episode2,
        string episode3)
    {
        return new Show
        {
            Id = id,
            Name = name,
            Description = description,
            EstimatedReach = estimatedReach,
            Episodes =
            [
                new Episode(episode1, episode1, now.AddDays(-5)),
                new Episode(episode2, episode2, now.AddDays(-12)),
                new Episode(episode3, episode3, now.AddDays(-19))
            ]
        };
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
