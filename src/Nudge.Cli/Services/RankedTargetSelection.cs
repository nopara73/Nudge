using Nudge.Cli.Models;

namespace Nudge.Cli.Services;

public static class RankedTargetSelection
{
    public static IReadOnlyList<RankedTarget> SelectTop(IReadOnlyList<RankedTarget> rankedResults, int top)
    {
        return rankedResults.Take(top).ToArray();
    }
}
