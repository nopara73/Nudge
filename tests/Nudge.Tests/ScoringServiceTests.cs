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
            Name = "AI Growth Weekly",
            EstimatedReach = 0.7,
            Episodes =
            [
                new Episode("AI for B2B onboarding", "Tactics for startups", new DateTimeOffset(2026, 2, 24, 0, 0, 0, TimeSpan.Zero)),
                new Episode("Growth experiments", "AI and SaaS loops", new DateTimeOffset(2026, 2, 17, 0, 0, 0, TimeSpan.Zero)),
                new Episode("Founder GTM", "Startups and positioning", new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero))
            ]
        };

        var result = service.Score(show, ["ai", "startups"]);
        var expected = (result.Reach * 0.4) + (result.Frequency * 0.3) + (result.NicheFit * 0.3);

        Assert.InRange(result.Reach, 0, 1);
        Assert.InRange(result.Frequency, 0, 1);
        Assert.InRange(result.NicheFit, 0, 1);
        Assert.Equal(expected, result.Score, 10);
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
        Assert.Equal(0.2, result.Score, 10);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
