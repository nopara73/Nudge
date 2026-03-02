using Nudge.Cli.Services;
using Nudge.Core.Models;

namespace Nudge.Tests;

public sealed class EpisodeTranscriptServiceTests
{
    [Fact]
    public async Task PopulateTranscriptAsync_UsesFeedTranscript_WhenTranscriptUrlExists()
    {
        var transcriptClient = new StubTranscriptContentClient("Published transcript text.");
        var sttTranscriber = new StubEpisodeSttTranscriber("Fallback transcript text.");
        var service = new EpisodeTranscriptService(transcriptClient, sttTranscriber, new HostTranscriptLineExtractor());
        var episode = new Episode(
            "Episode 1",
            "Description",
            DateTimeOffset.UtcNow,
            TranscriptUrl: "https://example.com/transcript.txt",
            AudioUrl: "https://example.com/audio.mp3");

        var resolved = await service.PopulateTranscriptAsync(episode, ["Host One"]);

        Assert.Equal("Published transcript text.", resolved.Transcript);
        Assert.Empty(resolved.HostTranscriptLines ?? Array.Empty<string>());
        Assert.Equal(1, transcriptClient.DownloadCalls);
        Assert.Equal(0, sttTranscriber.TranscribeCalls);
    }

    [Fact]
    public async Task PopulateTranscriptAsync_UsesSttFallback_WhenPublishedTranscriptUnavailable()
    {
        var transcriptClient = new StubTranscriptContentClient(null);
        var sttTranscriber = new StubEpisodeSttTranscriber("Host One: Fallback transcript text.");
        var service = new EpisodeTranscriptService(transcriptClient, sttTranscriber, new HostTranscriptLineExtractor());
        var episode = new Episode(
            "Episode 1",
            "Description",
            DateTimeOffset.UtcNow,
            TranscriptUrl: "https://example.com/transcript.txt",
            AudioUrl: "https://example.com/audio.mp3");

        var resolved = await service.PopulateTranscriptAsync(episode, ["Host One"]);

        Assert.Equal("Host One: Fallback transcript text.", resolved.Transcript);
        Assert.Equal(["Fallback transcript text."], resolved.HostTranscriptLines);
        Assert.Equal(1, transcriptClient.DownloadCalls);
        Assert.Equal(1, sttTranscriber.TranscribeCalls);
    }

    private sealed class StubTranscriptContentClient(string? response) : ITranscriptContentClient
    {
        private readonly string? _response = response;
        public int DownloadCalls { get; private set; }

        public Task<string?> DownloadTranscriptAsync(string transcriptUrl, CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            return Task.FromResult(_response);
        }
    }

    private sealed class StubEpisodeSttTranscriber(string? transcript) : IEpisodeSttTranscriber
    {
        private readonly string? _transcript = transcript;
        public int TranscribeCalls { get; private set; }

        public Task<string?> TranscribeAsync(Episode episode, CancellationToken cancellationToken = default)
        {
            TranscribeCalls++;
            return Task.FromResult(_transcript);
        }
    }
}
