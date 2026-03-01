namespace Nudge.Ui.Models;

public sealed record QueueEpisode
{
    public required string Title { get; init; }
    public string? Url { get; init; }
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
}
