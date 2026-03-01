namespace Nudge.Ui.Models;

public sealed record RunConfigProfile(
    IReadOnlyList<string> Keywords,
    int PublishedAfterDays,
    int Top,
    bool UseMock,
    bool Verbose);
