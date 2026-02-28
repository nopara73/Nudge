namespace Nudge.Cli.Models;

public sealed record PodchaserOptions
{
    public const string SectionName = "Podchaser";

    public string? DevelopmentToken { get; init; }
    public string? ProductionToken { get; init; }
}
