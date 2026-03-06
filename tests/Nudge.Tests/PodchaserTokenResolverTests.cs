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

    [Fact]
    public void ResolveLabeled_PreservesConfiguredLabelsAfterDeduplication()
    {
        var options = new PodchaserOptions
        {
            Token = "primary-token",
            FallbackTokens = ["primary-token", "fallback-token", "fallback-token-2"]
        };

        var ordered = PodchaserTokenResolver.ResolveLabeled(options);

        Assert.Collection(
            ordered,
            item =>
            {
                Assert.Equal("primary", item.Label);
                Assert.Equal("primary-token", item.Value);
            },
            item =>
            {
                Assert.Equal("fallback-1", item.Label);
                Assert.Equal("fallback-token", item.Value);
            },
            item =>
            {
                Assert.Equal("fallback-2", item.Label);
                Assert.Equal("fallback-token-2", item.Value);
            });
    }
}
