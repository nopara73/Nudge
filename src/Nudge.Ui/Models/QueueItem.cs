namespace Nudge.Ui.Models;

public sealed class QueueItem
{
    public required string IdentityKey { get; init; }
    public required string ShowId { get; init; }
    public required string ShowName { get; init; }
    public string? ContactEmail { get; init; }
    public string? ManualContactEmail { get; init; }
    public required string EffectiveContactEmail { get; init; }
    public required string DetectedLanguage { get; init; }
    public string FeedUrl { get; init; } = string.Empty;
    public double Score { get; init; }
    public double Reach { get; init; }
    public double Frequency { get; init; }
    public double NicheFit { get; init; }
    public double ActivityScore { get; init; }
    public string OutreachPriority { get; init; } = "Low";
    public DateTimeOffset? NewestEpisodePublishedAtUtc { get; init; }
    public IReadOnlyList<QueueEpisode> RecentEpisodes { get; init; } = Array.Empty<QueueEpisode>();
    public IReadOnlyList<string> RecentEpisodeTitles => RecentEpisodes.Select(episode => episode.Title).ToArray();
    public string NicheFitBreakdownJson { get; init; } = string.Empty;
    public OutreachState State { get; init; } = OutreachState.New;
    public DateTimeOffset? CooldownUntilUtc { get; init; }
    public DateTimeOffset? SnoozeUntilUtc { get; init; }
    public DateTimeOffset? ContactedAtUtc { get; init; }
    public string Tags { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
}
