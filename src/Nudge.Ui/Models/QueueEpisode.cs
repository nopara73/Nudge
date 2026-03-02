namespace Nudge.Ui.Models;

public sealed record QueueEpisode
{
    public required string Title { get; init; }
    public string? Url { get; init; }
    public string? AudioUrl { get; init; }
    public DateTimeOffset? PublishedAtUtc { get; init; }
    public string? TranscriptUrl { get; init; }
    public string? Transcript { get; init; }
    public IReadOnlyList<string> HostTranscriptLines { get; init; } = Array.Empty<string>();
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasNoUrl => !HasUrl;
    public bool HasPublishedAtUtc => PublishedAtUtc.HasValue;
    public bool HasNoPublishedAtUtc => !HasPublishedAtUtc;
    public bool HasTranscriptCandidate => !string.IsNullOrWhiteSpace(Transcript) || !string.IsNullOrWhiteSpace(TranscriptUrl);
    public bool HasHostTranscriptLines => HostTranscriptLines.Count > 0;
}
