namespace Nudge.Cli.Models;

public sealed record JsonOutputEnvelope
{
    public string SchemaVersion { get; init; } = "1.0";
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public required CliArguments Arguments { get; init; }
    public int Total { get; init; }
    public required IReadOnlyList<RankedTarget> Results { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
