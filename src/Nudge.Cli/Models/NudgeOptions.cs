namespace Nudge.Cli.Models;

public sealed record NudgeOptions
{
    public const string DefaultBaseUrl = "https://api.podchaser.com/";

    public string? ApiKey { get; init; }
    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public int PublishedAfterDays { get; init; } = 60;
}
