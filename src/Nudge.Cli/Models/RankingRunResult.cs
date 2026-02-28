namespace Nudge.Cli.Models;

public sealed record RankingRunResult
{
    public required IReadOnlyList<RankedTarget> Results { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
