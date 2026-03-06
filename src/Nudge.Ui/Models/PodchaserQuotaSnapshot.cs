namespace Nudge.Ui.Models;

public sealed record PodchaserQuotaSnapshot
{
    public DateTimeOffset CheckedAtUtc { get; init; }
    public IReadOnlyList<PodchaserQuotaTokenStatus> Tokens { get; init; } = Array.Empty<PodchaserQuotaTokenStatus>();
}

public sealed record PodchaserQuotaTokenStatus
{
    public string Label { get; init; } = string.Empty;
    public bool IsSuccessful { get; init; }
    public int? StatusCode { get; init; }
    public int? RemainingPoints { get; init; }
    public int? PreviewCost { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public string Detail { get; init; } = string.Empty;
}

public sealed record PodchaserQuotaTokenDisplayItem(
    string Label,
    string RemainingDisplay,
    string Detail,
    string BadgeText,
    string BadgeBackground,
    string BadgeForeground);
