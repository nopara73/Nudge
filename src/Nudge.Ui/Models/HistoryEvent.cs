namespace Nudge.Ui.Models;

public sealed class HistoryEvent
{
    public long Id { get; init; }
    public required string IdentityKey { get; init; }
    public string? ShowName { get; init; }
    public string? EffectiveContactEmail { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string FromState { get; init; } = string.Empty;
    public string ToState { get; init; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; init; }
    public string Note { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
}
