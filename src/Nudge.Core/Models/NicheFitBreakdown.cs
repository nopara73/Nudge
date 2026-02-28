namespace Nudge.Core.Models;

public sealed record NicheFitBreakdown
{
    public required IReadOnlyList<NicheFitTokenHit> TokenHits { get; init; }
    public double WeightedScore { get; init; }
    public double NormalizedScore { get; init; }
    public double PositiveContribution { get; init; }
    public double PenaltyMagnitude { get; init; }
    public int TotalMatchedTokens { get; init; }
    public bool BusinessContextDetected { get; init; }
}

public sealed record NicheFitTokenHit
{
    public required string Token { get; init; }
    public int Hits { get; init; }
    public double Weight { get; init; }
    public double Contribution { get; init; }
}
