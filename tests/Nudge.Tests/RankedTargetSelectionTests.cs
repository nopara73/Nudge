using Nudge.Cli.Models;
using Nudge.Cli.Services;

namespace Nudge.Tests;

public sealed class RankedTargetSelectionTests
{
    [Fact]
    public void FilterByReach_FiltersInclusiveBounds_AndReportsFilteredCount()
    {
        var input = new[]
        {
            BuildTarget("show-1", 0.2),
            BuildTarget("show-2", 0.5),
            BuildTarget("show-3", 0.9)
        };

        var (filtered, filteredOut) = RankedTargetSelection.FilterByReach(input, minReach: 0.5, maxReach: 0.9);

        Assert.Equal(1, filteredOut);
        Assert.Equal(new[] { "show-2", "show-3" }, filtered.Select(r => r.ShowId).ToArray());
    }

    [Fact]
    public void FilterByReach_WhenNoBounds_ReturnsOriginalAndZeroFiltered()
    {
        var input = new[]
        {
            BuildTarget("show-1", 0.2),
            BuildTarget("show-2", 0.5)
        };

        var (filtered, filteredOut) = RankedTargetSelection.FilterByReach(input, minReach: null, maxReach: null);

        Assert.Equal(0, filteredOut);
        Assert.Equal(input.Select(i => i.ShowId), filtered.Select(f => f.ShowId));
    }

    private static RankedTarget BuildTarget(string showId, double reach)
    {
        return new RankedTarget
        {
            ShowId = showId,
            ShowName = showId,
            DetectedLanguage = "en",
            Reach = reach,
            Frequency = 0.5,
            NicheFit = 0.5,
            ActivityScore = 0.5,
            Score = 0.5
        };
    }
}
