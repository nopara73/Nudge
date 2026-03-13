namespace Nudge.Core.Models;

public sealed record RssParsePayload
{
    public string? PodcastEmail { get; init; }
    public string? PodcastEmailSource { get; init; }
    public string? PodcastLanguage { get; init; }
    public IReadOnlyList<string> PodcastHosts { get; init; } = Array.Empty<string>();
    public IReadOnlyList<Episode> Episodes { get; init; } = Array.Empty<Episode>();
}
