namespace Nudge.Core.Models;

public sealed record PodcastSearchResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public required string FeedUrl { get; init; }
    public double EstimatedReach { get; init; }
    public ContactMethod ContactMethod { get; init; } = ContactMethod.None;
    public string? ContactValue { get; init; }
}
