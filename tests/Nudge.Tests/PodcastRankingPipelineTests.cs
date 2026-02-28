using Nudge.Cli.Models;
using Nudge.Cli.Services;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;
using Nudge.Core.Services;

namespace Nudge.Tests;

public sealed class PodcastRankingPipelineTests
{
    [Fact]
    public async Task RunAsync_WhenShowHasOnlyStaleEpisodes_IncludesItForScoring()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var pipeline = BuildPipeline(
            now,
            [new Episode("Old Episode", "about ai", now.AddDays(-120))]);

        var result = await pipeline.RunAsync(
            new CliArguments(["ai"], PublishedAfterDays: 7, Top: 10, JsonOutput: false, PrettyJson: false),
            includeDebugDiagnostics: true);

        Assert.Single(result.Results);
        Assert.Contains(result.Diagnostics, w => w.Contains("Raw API shows before local filtering: 1", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Diagnostics, w => w.Contains("retrying without recency filter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_WhenNoRankedResults_EmitsRetryWithoutRecencyWarning()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var pipeline = BuildPipeline(
            now,
            []);

        var result = await pipeline.RunAsync(
            new CliArguments(["ai"], PublishedAfterDays: 30, Top: 10, JsonOutput: false, PrettyJson: false),
            includeDebugDiagnostics: true);

        Assert.Empty(result.Results);
        Assert.Contains(result.Diagnostics, w => w.Contains("Raw API shows before local filtering: 1", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, w => w.Contains("retrying without recency filter", StringComparison.OrdinalIgnoreCase));
    }

    private static PodcastRankingPipeline BuildPipeline(DateTimeOffset now, IReadOnlyList<Episode> episodes)
    {
        return new PodcastRankingPipeline(
            new StubPodcastSearchClient(),
            new StubRssFeedClient(),
            new StubRssParser(episodes),
            new ScoringService(new FixedTimeProvider(now)),
            new FixedTimeProvider(now));
    }

    private sealed class StubPodcastSearchClient : IPodcastSearchClient
    {
        public Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(
            IReadOnlyList<string> keywords,
            int publishedAfterDays,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<PodcastSearchResult> results =
            [
                new PodcastSearchResult
                {
                    Id = "listennotes:pod-1",
                    Name = "AI Show",
                    Description = "AI content",
                    FeedUrl = "https://example.com/feed.xml",
                    EstimatedReach = 0.5
                }
            ];

            return Task.FromResult(results);
        }
    }

    private sealed class StubRssFeedClient : IRssFeedClient
    {
        public Task<string> GetFeedXmlAsync(string feedUrl, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("<rss />");
        }
    }

    private sealed class StubRssParser(IReadOnlyList<Episode> episodes) : IRssParser
    {
        private readonly IReadOnlyList<Episode> _episodes = episodes;

        public Task<Result<RssParsePayload>> ParseAsync(string feedXml, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<RssParsePayload>.Ok(new RssParsePayload
            {
                PodcastEmail = "host@example.com",
                Episodes = _episodes
            }));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
