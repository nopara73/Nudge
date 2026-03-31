namespace Nudge.Cli.Models;

public sealed record CliArguments(
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> Keywords,
    int PublishedAfterDays,
    int Top,
    bool JsonOutput,
    bool PrettyJson,
    double? MinReach = null,
    double? MaxReach = null,
    bool SkipHardToReachOnes = false
);
