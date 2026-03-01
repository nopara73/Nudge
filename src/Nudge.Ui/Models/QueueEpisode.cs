namespace Nudge.Ui.Models;

public sealed record QueueEpisode
{
    public required string Title { get; init; }
    public string? Url { get; init; }
    public DateTimeOffset? PublishedAtUtc { get; init; }
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasNoUrl => !HasUrl;
    public bool HasPublishedAtUtc => PublishedAtUtc.HasValue;
    public bool HasNoPublishedAtUtc => !HasPublishedAtUtc;
}
