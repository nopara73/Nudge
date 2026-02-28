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
        var expectedBase = (result.Reach * 0.35) + (result.Frequency * 0.25) + (result.NicheFit * 0.40);
        var expected = expectedBase * result.ActivityScore;

        Assert.InRange(result.Reach, 0, 1);
        Assert.InRange(result.Frequency, 0, 1);
        Assert.InRange(result.NicheFit, 0, 1);
        Assert.InRange(result.ActivityScore, 0, 1);
        Assert.Equal(expected, result.Score, 10);
        Assert.NotEmpty(result.NicheFitBreakdown.TokenHits);
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "athlete" && t.Weight == 3.0);
        Assert.Equal(result.NicheFit, result.NicheFitBreakdown.NormalizedScore, 10);
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
        Assert.Equal(0.15, result.ActivityScore, 10);
        Assert.Equal(0.02625, result.Score, 10);
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

        Assert.InRange(result.NicheFit, 0, 1);
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "revenue" && t.Weight == -2.0);
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "wellness" && t.Weight == -0.75);
        Assert.True(result.NicheFitBreakdown.BusinessContextDetected);
        Assert.True(result.NicheFitBreakdown.PenaltyMagnitude > 0);
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

    [Fact]
    public void Score_NicheFit_AlwaysWithinZeroAndOne()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var noSignal = BuildShow("none", "General Show", "No relevant terms.", 0.6, now, "Episode one", "Episode two", "Episode three");
        var highSignal = BuildShow(
            "high",
            "Athlete performance training competition ranking",
            "Masters VO2 strength crossfit hyrox PR.",
            0.6,
            now,
            "Training athlete competition",
            "Performance ranking PR",
            "Strength VO2 masters");
        var businessHeavy = BuildShow(
            "penalty",
            "Fitness marketing revenue entrepreneur coaching business",
            "Wellness sales monetize clients.",
            0.6,
            now,
            "Marketing revenue coaching",
            "Entrepreneur wellness business",
            "Sales clients monetize");

        var scores = new[]
        {
            service.Score(noSignal, ["fitness"]),
            service.Score(highSignal, ["fitness"]),
            service.Score(businessHeavy, ["fitness"])
        };

        foreach (var score in scores)
        {
            Assert.InRange(score.NicheFit, 0, 1);
        }
    }

    [Fact]
    public void Score_MoreHighIntentTokens_IncreasesNicheFitMonotonically()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var low = BuildShow("low", "Longevity Fitness", "Aging healthspan.", 0.6, now, "Longevity update", "Fitness recap", "Aging notes");
        var medium = BuildShow(
            "medium",
            "Athlete Longevity Performance",
            "Fitness and training with masters focus.",
            0.6,
            now,
            "Training update",
            "Performance recap",
            "Longevity and fitness");
        var high = BuildShow(
            "high",
            "Athlete Masters Performance Training Competition Ranking",
            "Strength VO2 crossfit hyrox PR.",
            0.6,
            now,
            "Competition training",
            "PR and ranking",
            "Strength VO2 block");

        var lowScore = service.Score(low, ["longevity", "fitness"]);
        var mediumScore = service.Score(medium, ["longevity", "fitness"]);
        var highScore = service.Score(high, ["longevity", "fitness"]);

        Assert.True(lowScore.NicheFit < mediumScore.NicheFit);
        Assert.True(mediumScore.NicheFit < highScore.NicheFit);
    }

    [Fact]
    public void Score_PenaltyTokens_DecreaseNicheFit_ButNeverBelowZero()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var noPenalty = BuildShow(
            "no-penalty",
            "Athlete Performance Training",
            "Masters strength and VO2 focus.",
            0.6,
            now,
            "Competition prep",
            "Training block",
            "Performance review");
        var withPenalty = BuildShow(
            "with-penalty",
            "Athlete Performance Training Revenue Marketing",
            "Masters strength and VO2 with coaching business wellness.",
            0.6,
            now,
            "Competition prep and marketing",
            "Training block revenue",
            "Performance review coaching");

        var noPenaltyScore = service.Score(noPenalty, ["fitness"]);
        var withPenaltyScore = service.Score(withPenalty, ["fitness"]);

        Assert.True(withPenaltyScore.NicheFit < noPenaltyScore.NicheFit);
        Assert.True(withPenaltyScore.NicheFit >= 0);
    }

    [Fact]
    public void Score_RecentPerformanceEpisodeTitles_BoostNicheFit_OverTheoryOnlyTitles()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var theoryHeavyTitles = BuildShow(
            id: "theory-heavy",
            name: "Longevity Athlete Lab",
            description: "Athlete performance and healthspan perspective.",
            estimatedReach: 0.65,
            now,
            "Longevity theory deep dive",
            "Healthspan mechanism review",
            "Aging pathways explained");
        var performanceHeavyTitles = BuildShow(
            id: "performance-heavy",
            name: "Longevity Athlete Lab",
            description: "Athlete performance and healthspan perspective.",
            estimatedReach: 0.65,
            now,
            "Competition training block",
            "VO2 and strength progression",
            "Masters race prep");

        var theoryScore = service.Score(theoryHeavyTitles, ["longevity", "fitness"]);
        var performanceScore = service.Score(performanceHeavyTitles, ["longevity", "fitness"]);

        Assert.True(performanceScore.NicheFit > theoryScore.NicheFit);
        Assert.True(performanceScore.Score > theoryScore.Score);
    }

    [Fact]
    public void Score_ActivityScore_UsesDeterministicRecencyBuckets()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var d30 = BuildShow("d30", "Show 30", "desc", 0.6, now, "ep1", "ep2", "ep3");
        var d90 = new Show
        {
            Id = "d90",
            Name = "Show 90",
            Description = "desc",
            EstimatedReach = 0.6,
            Episodes = [new Episode("ep", "ep", now.AddDays(-75))]
        };
        var d180 = new Show
        {
            Id = "d180",
            Name = "Show 180",
            Description = "desc",
            EstimatedReach = 0.6,
            Episodes = [new Episode("ep", "ep", now.AddDays(-140))]
        };
        var stale = new Show
        {
            Id = "stale",
            Name = "Stale",
            Description = "desc",
            EstimatedReach = 0.6,
            Episodes = [new Episode("ep", "ep", now.AddDays(-240))]
        };

        Assert.Equal(1.0, service.Score(d30, ["x"]).ActivityScore, 10);
        Assert.Equal(0.7, service.Score(d90, ["x"]).ActivityScore, 10);
        Assert.Equal(0.4, service.Score(d180, ["x"]).ActivityScore, 10);
        Assert.Equal(0.15, service.Score(stale, ["x"]).ActivityScore, 10);
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
