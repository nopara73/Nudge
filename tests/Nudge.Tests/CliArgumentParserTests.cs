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
}
