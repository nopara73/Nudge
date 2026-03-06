using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class CliArgumentParserTests
{
    [Fact]
    public void TryParse_AllowsUseMockFlagWithoutChangingArgumentsPayload()
    {
        var result = CliArgumentParser.TryParse(["--keywords", "ai,startups", "--published-after-days", "30", "--use-mock"]);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(2, result.Payload!.Keywords.Count);
        Assert.Equal(30, result.Payload.PublishedAfterDays);
    }

    [Fact]
    public void TryParse_AllowsVerboseFlagWithoutChangingArgumentsPayload()
    {
        var result = CliArgumentParser.TryParse(["--keywords", "ai,startups", "--published-after-days", "30", "--verbose"]);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(2, result.Payload!.Keywords.Count);
        Assert.Equal(30, result.Payload.PublishedAfterDays);
    }

    [Fact]
    public void TryParse_ParsesReachBounds_WhenProvided()
    {
        var result = CliArgumentParser.TryParse(
            ["--keywords", "ai,startups", "--published-after-days", "30", "--min-reach", "0.25", "--max-reach", "0.9"]);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(0.25, result.Payload!.MinReach!.Value, 3);
        Assert.Equal(0.9, result.Payload.MaxReach!.Value, 3);
    }

    [Fact]
    public void TryParse_Fails_WhenReachBoundsAreOutOfRange()
    {
        var result = CliArgumentParser.TryParse(
            ["--keywords", "ai,startups", "--published-after-days", "30", "--min-reach", "1.2"]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == "invalid_min_reach");
    }

    [Fact]
    public void TryParse_Fails_WhenMinReachIsGreaterThanMaxReach()
    {
        var result = CliArgumentParser.TryParse(
            ["--keywords", "ai,startups", "--published-after-days", "30", "--min-reach", "0.8", "--max-reach", "0.3"]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Code == "invalid_reach_bounds");
    }
}
