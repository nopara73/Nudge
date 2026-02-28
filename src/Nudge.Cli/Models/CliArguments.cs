namespace Nudge.Cli.Models;

public sealed record CliArguments(
    IReadOnlyList<string> Keywords,
    int PublishedAfterDays,
    int Top,
    bool JsonOutput,
    bool PrettyJson
);
