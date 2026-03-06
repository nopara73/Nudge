using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class PodchaserTokenMemoryTests
{
    [Fact]
    public void PrioritizeRememberedToken_MovesRememberedTokenToFront()
    {
        var ordered = PodchaserTokenMemory.PrioritizeRememberedToken(
            ["prod", "dev", "fallback"],
            "dev");

        Assert.Equal(["dev", "prod", "fallback"], ordered);
    }

    [Fact]
    public void PrioritizeRememberedToken_LeavesOrderWhenRememberedTokenMissing()
    {
        var ordered = PodchaserTokenMemory.PrioritizeRememberedToken(
            ["prod", "dev", "fallback"],
            "unknown");

        Assert.Equal(["prod", "dev", "fallback"], ordered);
    }

    [Fact]
    public void RememberToken_ThenLoadLastKnownGoodToken_RoundTrips()
    {
        var tempFile = Path.Combine(
            Path.GetTempPath(),
            "nudge-tests",
            Guid.NewGuid().ToString("N"),
            "podchaser-token.txt");
        var memory = new PodchaserTokenMemory(tempFile);

        memory.RememberToken("token.part.one");
        var loaded = memory.LoadLastKnownGoodToken();

        Assert.Equal("token.part.one", loaded);
    }
}
