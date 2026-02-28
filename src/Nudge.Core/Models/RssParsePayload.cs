namespace Nudge.Core.Models;

public sealed record RssParsePayload
{
    public string? PodcastEmail { get; init; }
    public IReadOnlyList<Episode> Episodes { get; init; } = Array.Empty<Episode>();
}
