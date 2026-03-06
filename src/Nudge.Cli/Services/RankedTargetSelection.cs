using Nudge.Cli.Models;

namespace Nudge.Cli.Services;

public static class RankedTargetSelection
{
    public static (IReadOnlyList<RankedTarget> Filtered, int FilteredOut) FilterByReach(
        IReadOnlyList<RankedTarget> rankedResults,
        double? minReach,
        double? maxReach)
    {
        if (!minReach.HasValue && !maxReach.HasValue)
        {
            return (rankedResults, 0);
        }

        var filtered = rankedResults
            .Where(r =>
            {
                if (minReach.HasValue && r.Reach < minReach.Value)
                {
                    return false;
                }

                if (maxReach.HasValue && r.Reach > maxReach.Value)
                {
                    return false;
                }

                return true;
            })
            .ToArray();

        return (filtered, rankedResults.Count - filtered.Length);
    }

    public static IReadOnlyList<RankedTarget> SelectTop(IReadOnlyList<RankedTarget> rankedResults, int top)
    {
        return rankedResults.Take(top).ToArray();
    }
}
