namespace Nudge.Cli.Services;

public interface IHostTranscriptLineExtractor
{
    IReadOnlyList<string> ExtractHostLines(string? transcript, IReadOnlyList<string> podcastHosts);
}
