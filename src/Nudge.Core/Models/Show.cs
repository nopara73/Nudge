namespace Nudge.Core.Models;

public sealed record Show
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = string.Empty;
    public string FeedUrl { get; init; } = string.Empty;
    public double EstimatedReach { get; init; }
    public ContactMethod ContactMethod { get; init; } = ContactMethod.None;
    public string? ContactValue { get; init; }
    public IReadOnlyList<Episode> Episodes { get; init; } = Array.Empty<Episode>();
}
