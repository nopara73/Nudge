using Nudge.Core.Models;

namespace Nudge.Cli.Models;

public sealed record RankedTarget
{
    public required string ShowId { get; init; }
    public required string ShowName { get; init; }
    public required string DetectedLanguage { get; init; }
    public string FeedUrl { get; init; } = string.Empty;
    public string? ContactEmail { get; init; }
    public double Reach { get; init; }
    public double Frequency { get; init; }
    public double NicheFit { get; init; }
    public double ActivityScore { get; init; }
    public NicheFitBreakdown? NicheFitBreakdown { get; init; }
    public double Score { get; init; }
    public DateTimeOffset? NewestEpisodePublishedAtUtc { get; init; }
    public IReadOnlyList<string> RecentEpisodeTitles { get; init; } = Array.Empty<string>();
}
