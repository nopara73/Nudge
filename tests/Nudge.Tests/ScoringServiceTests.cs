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
            Description = "Athlete-focused strength, VO2, and fitness sessions.",
            EstimatedReach = 0.7,
            Episodes =
            [
                new Episode("Competition prep", "Athlete training block", new DateTimeOffset(2026, 2, 24, 0, 0, 0, TimeSpan.Zero)),
                new Episode("Crossfit PR recap", "Strength and ranking analysis", new DateTimeOffset(2026, 2, 17, 0, 0, 0, TimeSpan.Zero)),
                new Episode("Hyrox strategy", "Masters race planning", new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero))
            ]
        };

        var result = service.Score(show, ["fitness"]);
        var expectedBase = (result.Reach * 0.35) + (result.Frequency * 0.25) + (result.NicheFit * 0.40);
        var expected = expectedBase * result.ActivityScore;

        Assert.InRange(result.Reach, 0, 1);
        Assert.InRange(result.Frequency, 0, 1);
        Assert.InRange(result.NicheFit, 0, 1);
        Assert.InRange(result.ActivityScore, 0, 1);
        Assert.Equal(expected, result.Score, 10);
        Assert.NotEmpty(result.NicheFitBreakdown.TokenHits);
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "fitness" && t.Weight == 1.0);
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
        Assert.Equal(0.03, result.ActivityScore, 10);
        Assert.Equal(0.00525, result.Score, 10);
    }

    [Fact]
    public void Score_QueryDrivenTerms_DoNotRewardUnaskedSynonyms()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var directMatchShow = BuildShow(
            id: "direct-match",
            name: "Performance Weekly",
            description: "Athlete strength and VO2 development.",
            estimatedReach: 0.65,
            now,
            "Training for competition",
            "Crossfit PR improvements",
            "Ranking and performance review");
        var adjacentConceptShow = BuildShow(
            id: "adjacent-concept",
            name: "Longevity Science Weekly",
            description: "Longevity and healthspan science updates.",
            estimatedReach: 0.65,
            now,
            "Aging pathways overview",
            "Healthspan biomarkers",
            "Longevity interview");

        var directMatchScore = service.Score(directMatchShow, ["performance"]);
        var adjacentConceptScore = service.Score(adjacentConceptShow, ["performance"]);

        Assert.True(directMatchScore.NicheFit > adjacentConceptScore.NicheFit);
        Assert.Equal(0, adjacentConceptScore.NicheFit, 10);
    }

    [Fact]
    public void Score_BusinessFocusedFitnessShow_UsesOnlyQueryTermsForNicheFit()
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
        Assert.Contains(result.NicheFitBreakdown.TokenHits, t => t.Token == "fitness" && t.Weight == 1.0);
        Assert.DoesNotContain(result.NicheFitBreakdown.TokenHits, t => t.Token == "revenue");
        Assert.DoesNotContain(result.NicheFitBreakdown.TokenHits, t => t.Token == "wellness");
        Assert.False(result.NicheFitBreakdown.BusinessContextDetected);
        Assert.Equal(0, result.NicheFitBreakdown.PenaltyMagnitude, 10);
    }

    [Fact]
    public void Score_PureLongevityScience_OutranksPerformanceHeavy_ForLongevityOnlyQuery()
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
            name: "Performance Lab",
            description: "Athlete strength training and VO2 optimization.",
            estimatedReach: 0.7,
            now,
            "Competition prep",
            "PR progress review",
            "Masters training block");

        var longevityScore = service.Score(longevityShow, ["longevity"]);
        var performanceScore = service.Score(performanceShow, ["longevity"]);

        Assert.True(longevityScore.NicheFit > 0);
        Assert.True(longevityScore.NicheFit >= performanceScore.NicheFit);
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
    public void Score_MoreQueryTermCoverage_IncreasesNicheFitMonotonically()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var low = BuildShow("low", "Longevity Weekly", "General update.", 0.6, now, "Longevity update", "General recap", "General notes");
        var medium = BuildShow(
            "medium",
            "Longevity Fitness Weekly",
            "Fitness updates and longevity notes.",
            0.6,
            now,
            "Longevity update",
            "Fitness recap",
            "General notes");
        var high = BuildShow(
            "high",
            "Longevity Fitness Aging Weekly",
            "Longevity fitness aging insights.",
            0.6,
            now,
            "Longevity fitness aging",
            "Aging longevity notes",
            "Fitness longevity recap");

        var lowScore = service.Score(low, ["longevity", "fitness", "aging"]);
        var mediumScore = service.Score(medium, ["longevity", "fitness", "aging"]);
        var highScore = service.Score(high, ["longevity", "fitness", "aging"]);

        Assert.True(lowScore.NicheFit < mediumScore.NicheFit);
        Assert.True(mediumScore.NicheFit < highScore.NicheFit);
    }

    [Fact]
    public void Score_ExtraBusinessWords_DoNotReduceNicheFit_WhenQueryCoverageMatches()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var plainMatch = BuildShow(
            "plain-match",
            "Fitness Focus",
            "Fitness focus with no business context.",
            0.6,
            now,
            "Fitness prep",
            "Fitness block",
            "Fitness review");
        var extraBusinessWords = BuildShow(
            "extra-business-words",
            "Fitness Revenue Marketing Weekly",
            "Fitness talk with coaching business wellness.",
            0.6,
            now,
            "Fitness prep and marketing",
            "Fitness block revenue",
            "Fitness review coaching");

        var plainMatchScore = service.Score(plainMatch, ["fitness"]);
        var extraBusinessWordsScore = service.Score(extraBusinessWords, ["fitness"]);

        Assert.Equal(plainMatchScore.NicheFit, extraBusinessWordsScore.NicheFit, 10);
        Assert.True(extraBusinessWordsScore.NicheFit >= 0);
    }

    [Fact]
    public void Score_RecentKeywordMatchedEpisodeTitles_BoostNicheFit_OverTheoryOnlyTitles()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var theoryHeavyTitles = BuildShow(
            id: "theory-heavy",
            name: "Longevity Lab",
            description: "Longevity and healthspan perspective.",
            estimatedReach: 0.65,
            now,
            "Longevity theory deep dive",
            "Healthspan mechanism review",
            "Aging pathways explained");
        var performanceHeavyTitles = BuildShow(
            id: "performance-heavy",
            name: "Longevity Lab",
            description: "Longevity and healthspan perspective.",
            estimatedReach: 0.65,
            now,
            "Longevity and fitness progression",
            "Healthspan and aging markers",
            "Longevity fitness habits");

        var theoryScore = service.Score(theoryHeavyTitles, ["longevity", "fitness"]);
        var performanceScore = service.Score(performanceHeavyTitles, ["longevity", "fitness"]);

        Assert.True(performanceScore.NicheFit > theoryScore.NicheFit);
        Assert.True(performanceScore.Score > theoryScore.Score);
    }

    [Fact]
    public void Score_MultiWordKeywords_RequireExactNormalizedPhraseMatches()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var exactPhraseShow = BuildShow(
            id: "exact-phrase",
            name: "VO2 Max Weekly",
            description: "Exact VO2 max training discussions.",
            estimatedReach: 0.62,
            now,
            "VO2 max intervals",
            "VO2 max pacing",
            "VO2 max recovery");
        var splitTokenShow = BuildShow(
            id: "split-token",
            name: "VO2 Performance Weekly",
            description: "VO2 training with max effort intervals.",
            estimatedReach: 0.62,
            now,
            "VO2 intervals",
            "Max effort training",
            "VO2 pacing");

        var exactPhraseScore = service.Score(exactPhraseShow, ["vo2 max"]);
        var splitTokenScore = service.Score(splitTokenShow, ["vo2 max"]);

        Assert.True(exactPhraseScore.NicheFit > splitTokenScore.NicheFit);
        Assert.Equal(0, splitTokenScore.NicheFit, 10);
        Assert.Contains(exactPhraseScore.NicheFitBreakdown.TokenHits, t => t.Token == "vo2 max");
    }

    [Fact]
    public void Score_KeywordAlignment_DemotesShowsMatchingOnlyGenericTerms()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var query = new[]
        {
            "masters athlete",
            "over 40 fitness",
            "longevity performance",
            "crossfit masters",
            "vo2 max aging",
            "rejuvenation olympics"
        };
        var relevant = BuildShow(
            id: "relevant",
            name: "Masters Athlete Performance Lab",
            description: "Over 40 fitness with VO2 training and longevity performance.",
            estimatedReach: 0.62,
            now,
            "Crossfit masters training block",
            "VO2 max aging and pacing",
            "Rejuvenation olympics prep");
        var genericTermOnly = BuildShow(
            id: "generic-term",
            name: "Gear Masters",
            description: "Guitar pedals and amp deep dives.",
            estimatedReach: 0.62,
            now,
            "Best pedals this week",
            "Tube amp maintenance",
            "Studio guitar setup");

        var relevantScore = service.Score(relevant, query);
        var genericScore = service.Score(genericTermOnly, query);

        Assert.True(relevantScore.NicheFit > genericScore.NicheFit);
        Assert.True(relevantScore.Score > genericScore.Score);
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
        Assert.Equal(0.03, service.Score(stale, ["x"]).ActivityScore, 10);
    }

    [Fact]
    public void Score_Reach_DoesNotIncreaseBeyondSeededReach()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var service = new ScoringService(new FixedTimeProvider(now));
        var show = BuildShow(
            id: "reach-check",
            name: "Recent small show",
            description: "Fresh episodes but unknown audience size.",
            estimatedReach: 0.2,
            now,
            "Episode one",
            "Episode two",
            "Episode three");

        var result = service.Score(show, ["fitness"]);

        Assert.True(result.Reach <= 0.2 + 1e-10);
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
