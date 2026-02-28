using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class PodcastSearchClientModeResolverTests
{
    [Theory]
    [InlineData(true, null, "token.part.one", true, false)]
    [InlineData(false, true, "token.part.one", true, false)]
    [InlineData(true, false, "token.part.one", false, false)]
    [InlineData(false, null, null, true, true)]
    [InlineData(false, null, "not-a-bearer-token", true, true)]
    [InlineData(false, null, "segment1.segment2", true, true)]
    [InlineData(false, null, "token.part.one", false, false)]
    public void ResolveUseMock_AppliesPrecedenceAndApiKeyFallback(
        bool cliUseMock,
        bool? envUseMock,
        string? apiKey,
        bool expectedUseMock,
        bool expectedMissingApiKeyWarning)
    {
        var resolved = PodcastSearchClientModeResolver.ResolveUseMock(cliUseMock, envUseMock, apiKey);

        Assert.Equal(expectedUseMock, resolved.UseMock);
        Assert.Equal(expectedMissingApiKeyWarning, resolved.MissingApiKeyWarning);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void TryParseUseMockValue_ParsesSupportedValues(string raw, bool expected)
    {
        var parsed = PodcastSearchClientModeResolver.TryParseUseMockValue(raw);
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("nope")]
    [InlineData("")]
    public void TryParseUseMockValue_ReturnsNullForUnsupportedValues(string raw)
    {
        var parsed = PodcastSearchClientModeResolver.TryParseUseMockValue(raw);
        Assert.Null(parsed);
    }
}
