namespace Nudge.Cli.Models;

public sealed record NudgeOptions
{
    public const string DefaultBaseUrl = "https://listen-api.listennotes.com/api/v2/";

    public string? ApiKey { get; init; }
    public string BaseUrl { get; init; } = DefaultBaseUrl;
    public int PublishedAfterDays { get; init; } = 60;
}
