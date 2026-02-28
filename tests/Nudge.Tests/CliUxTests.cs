using System.Globalization;
using Nudge.Cli.Models;
using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class CliUxTests
{
    [Fact]
    public void TableRenderer_UsesInvariantDecimalFormatting()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var french = CultureInfo.GetCultureInfo("fr-FR");
            CultureInfo.CurrentCulture = french;
            CultureInfo.CurrentUICulture = french;

            var result = new RankedTarget
            {
                ShowId = "show-1",
                ShowName = "AI Show",
                DetectedLanguage = "en",
                ContactEmail = "host@example.com",
                Reach = 0.333333,
                Frequency = 0.666666,
                NicheFit = 0.555555,
                Score = 0.7494,
                NewestEpisodePublishedAtUtc = new DateTimeOffset(2026, 2, 28, 7, 0, 0, TimeSpan.Zero)
            };

            using var writer = new StringWriter();
            RankedTargetTableRenderer.Write([result], writer);
            var table = writer.ToString();

            Assert.Contains("0.749", table);
            Assert.DoesNotContain("0,749", table);
            Assert.Contains("2026-02-28", table);
            Assert.Contains("| Lang |", table);
            Assert.Contains("| en   |", table);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void TopSelection_AppliesRequestedLimitAfterRanking()
    {
        var ranked = new List<RankedTarget>
        {
            new() { ShowId = "s1", ShowName = "One", DetectedLanguage = "en", Score = 0.9, Reach = 0.9, Frequency = 0.9, NicheFit = 0.9 },
            new() { ShowId = "s2", ShowName = "Two", DetectedLanguage = "en", Score = 0.8, Reach = 0.8, Frequency = 0.8, NicheFit = 0.8 },
            new() { ShowId = "s3", ShowName = "Three", DetectedLanguage = "en", Score = 0.7, Reach = 0.7, Frequency = 0.7, NicheFit = 0.7 }
        };

        var limited = RankedTargetSelection.SelectTop(ranked, 2);

        Assert.Equal(2, limited.Count);
        Assert.Equal("s1", limited[0].ShowId);
        Assert.Equal("s2", limited[1].ShowId);
    }
}
