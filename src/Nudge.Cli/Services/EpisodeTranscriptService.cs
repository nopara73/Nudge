using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class EpisodeTranscriptService(
    ITranscriptContentClient transcriptContentClient,
    IEpisodeSttTranscriber sttTranscriber,
    IHostTranscriptLineExtractor hostTranscriptLineExtractor) : IEpisodeTranscriptService
{
    private readonly ITranscriptContentClient _transcriptContentClient = transcriptContentClient;
    private readonly IEpisodeSttTranscriber _sttTranscriber = sttTranscriber;
    private readonly IHostTranscriptLineExtractor _hostTranscriptLineExtractor = hostTranscriptLineExtractor;

    public async Task<Episode> PopulateTranscriptAsync(
        Episode episode,
        IReadOnlyList<string> podcastHosts,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(episode.Transcript))
        {
            return episode with
            {
                HostTranscriptLines = _hostTranscriptLineExtractor.ExtractHostLines(episode.Transcript, podcastHosts)
            };
        }

        string? transcriptText = null;
        if (!string.IsNullOrWhiteSpace(episode.TranscriptUrl))
        {
            var transcriptFromFeed = await _transcriptContentClient.DownloadTranscriptAsync(episode.TranscriptUrl, cancellationToken);
            if (!string.IsNullOrWhiteSpace(transcriptFromFeed))
            {
                transcriptText = transcriptFromFeed.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            var fallbackTranscript = await _sttTranscriber.TranscribeAsync(episode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fallbackTranscript))
            {
                transcriptText = fallbackTranscript.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            return episode with { HostTranscriptLines = Array.Empty<string>() };
        }

        return episode with
        {
            Transcript = transcriptText,
            HostTranscriptLines = _hostTranscriptLineExtractor.ExtractHostLines(transcriptText, podcastHosts)
        };
    }
}
