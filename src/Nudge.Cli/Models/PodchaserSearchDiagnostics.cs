namespace Nudge.Cli.Models;

public sealed record PodchaserSearchDiagnostics
{
    public static PodchaserSearchDiagnostics Empty { get; } = new();

    public bool Executed { get; init; }
    public int KeywordCount { get; init; }
    public int TargetResultCount { get; init; }
    public int TargetCandidateCount { get; init; }
    public int HttpRequestsSent { get; init; }
    public int SuccessfulPageCount { get; init; }
    public int RawCandidatesReturned { get; init; }
    public bool CacheHit { get; init; }
    public bool LegacyFallbackTriggered { get; init; }
    public bool ReducedPageSizeTriggered { get; init; }
    public bool EarlyExitTriggered { get; init; }
}
