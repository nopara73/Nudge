namespace Nudge.Cli.Models;

public sealed record PodchaserOptions
{
    public const string SectionName = "Podchaser";

    public string? Token { get; init; }
    public string[]? FallbackTokens { get; init; }
}
