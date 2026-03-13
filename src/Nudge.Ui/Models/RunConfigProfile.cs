namespace Nudge.Ui.Models;

public sealed record RunConfigProfile(
    IReadOnlyList<string> SearchTerms,
    IReadOnlyList<string> Keywords,
    int PublishedAfterDays,
    int Top,
    double? MinReach,
    double? MaxReach,
    bool UseMock,
    bool Verbose);
