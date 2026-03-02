namespace Nudge.Cli.Services;

public interface ITranscriptContentClient
{
    Task<string?> DownloadTranscriptAsync(string transcriptUrl, CancellationToken cancellationToken = default);
}
