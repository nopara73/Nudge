namespace Nudge.Core.Models;

public sealed record IntentScore
{
    public required string ShowId { get; init; }
    public required string ShowName { get; init; }
    public double Reach { get; init; }
    public double Frequency { get; init; }
    public double NicheFit { get; init; }
    public double Score { get; init; }
    public DateTimeOffset? NewestEpisodePublishedAtUtc { get; init; }
    public string? ContactEmail { get; init; }
}
