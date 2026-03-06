using Nudge.Cli.Models;
using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class PodchaserTokenResolverTests
{
    [Fact]
    public void Resolve_ReturnsPrimaryThenFallbackTokens()
    {
        var options = new PodchaserOptions
        {
            Token = "primary",
            FallbackTokens = ["fallback-a", "fallback-b"]
        };

        var ordered = PodchaserTokenResolver.Resolve(options);

        Assert.Equal(["primary", "fallback-a", "fallback-b"], ordered);
    }

    [Fact]
    public void Resolve_DeduplicatesRepeatedTokens()
    {
        var options = new PodchaserOptions
        {
            Token = "primary",
            FallbackTokens = ["primary", "fallback", "fallback"]
        };

        var ordered = PodchaserTokenResolver.Resolve(options);

        Assert.Equal(["primary", "fallback"], ordered);
    }
}
