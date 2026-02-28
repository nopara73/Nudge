using Nudge.Cli.Models;
using Nudge.Cli.Services;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;
using Nudge.Core.Services;
using System.Net;

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

    [Fact]
    public async Task RunAsync_WithFixedFixtureData_IsDeterministic_AndPrioritizesNicheFit()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new PodcastSearchResult
            {
                Id = "podchaser:niche-fitness",
                Name = "Longevity Fitness Lab",
                Description = "Focused longevity and performance show.",
                FeedUrl = "https://feeds.example.com/niche.xml",
                EstimatedReach = 0.62
            },
            new PodcastSearchResult
            {
                Id = "podchaser:generic-fitness",
                Name = "Daily Fitness Roundup",
                Description = "General fitness headlines.",
                FeedUrl = "https://feeds.example.com/generic.xml",
                EstimatedReach = 0.95
            },
            new PodcastSearchResult
            {
                Id = "podchaser:tie-a",
                Name = "Wellness Weekly",
                Description = "General wellness updates.",
                FeedUrl = "https://feeds.example.com/tie-a.xml",
                EstimatedReach = 0.55
            },
            new PodcastSearchResult
            {
                Id = "podchaser:tie-b",
                Name = "Wellness Weekly",
                Description = "General wellness updates.",
                FeedUrl = "https://feeds.example.com/tie-b.xml",
                EstimatedReach = 0.55
            }
        };

        var payloads = new Dictionary<string, RssParsePayload>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://feeds.example.com/niche.xml"] = new RssParsePayload
            {
                PodcastEmail = null,
                PodcastLanguage = "en-US",
                Episodes = BuildRecentEpisodes(
                    now,
                    "Longevity for endurance athletes",
                    "Fitness protocol updates",
                    "Longevity + fitness Q&A")
            },
            ["https://feeds.example.com/generic.xml"] = new RssParsePayload
            {
                PodcastEmail = "host@generic.example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(
                    now,
                    "Top fitness gadgets",
                    "Gym routine myths",
                    "Protein trends")
            },
            ["https://feeds.example.com/tie-a.xml"] = new RssParsePayload
            {
                PodcastEmail = "a@wellness.example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(now, "Weekly roundup", "Weekly roundup", "Weekly roundup")
            },
            ["https://feeds.example.com/tie-b.xml"] = new RssParsePayload
            {
                PodcastEmail = "b@wellness.example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(now, "Weekly roundup", "Weekly roundup", "Weekly roundup")
            }
        };

        var pipeline = BuildPipeline(now, candidates, payloads);
        var args = new CliArguments(["longevity", "fitness"], PublishedAfterDays: 60, Top: 10, JsonOutput: false, PrettyJson: false);

        var run1 = await pipeline.RunAsync(args);
        var run2 = await pipeline.RunAsync(args);

        Assert.Equal(run1.Results.Select(r => r.ShowId), run2.Results.Select(r => r.ShowId));
        Assert.Equal("podchaser:niche-fitness", run1.Results[0].ShowId);
        Assert.Contains(run1.Warnings, w => w.Contains("Missing contact email penalty applied", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_WhenFeedsReturn404Or500_SkipsFailedFeedsAndKeepsStableRanking()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new PodcastSearchResult
            {
                Id = "podchaser:good",
                Name = "Healthy Longevity",
                Description = "High-signal longevity content.",
                FeedUrl = "https://feeds.example.com/good.xml",
                EstimatedReach = 0.75
            },
            new PodcastSearchResult
            {
                Id = "podchaser:not-found",
                Name = "Broken 404 Feed",
                Description = "Unavailable feed.",
                FeedUrl = "https://feeds.example.com/404.xml",
                EstimatedReach = 0.9
            },
            new PodcastSearchResult
            {
                Id = "podchaser:server-error",
                Name = "Broken 500 Feed",
                Description = "Unavailable feed.",
                FeedUrl = "https://feeds.example.com/500.xml",
                EstimatedReach = 0.9
            }
        };

        var payloads = new Dictionary<string, RssParsePayload>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://feeds.example.com/good.xml"] = new RssParsePayload
            {
                PodcastEmail = "hello@healthy.example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(now, "Longevity deep dive", "Fitness and aging", "Sleep science")
            }
        };

        var pipeline = BuildPipeline(
            now,
            candidates,
            payloads,
            failures: new Dictionary<string, HttpStatusCode>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://feeds.example.com/404.xml"] = HttpStatusCode.NotFound,
                ["https://feeds.example.com/500.xml"] = HttpStatusCode.InternalServerError
            });

        var result = await pipeline.RunAsync(
            new CliArguments(["longevity", "fitness"], PublishedAfterDays: 60, Top: 10, JsonOutput: false, PrettyJson: false));

        Assert.Single(result.Results);
        Assert.Equal("podchaser:good", result.Results[0].ShowId);
        Assert.Contains(result.Warnings, w => w.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, w => w.Contains("HTTP 500", StringComparison.OrdinalIgnoreCase));
    }

    private static PodcastRankingPipeline BuildPipeline(
        DateTimeOffset now,
        IReadOnlyList<PodcastSearchResult> candidates,
        IReadOnlyDictionary<string, RssParsePayload> payloads,
        IReadOnlyDictionary<string, HttpStatusCode>? failures = null)
    {
        return new PodcastRankingPipeline(
            new StubPodcastSearchClient(candidates),
            new StubRssFeedClient(payloads, failures),
            new StubRssParser(payloads),
            new ScoringService(new FixedTimeProvider(now)),
            new FixedTimeProvider(now));
    }

    private static PodcastRankingPipeline BuildPipeline(DateTimeOffset now, IReadOnlyList<Episode> episodes)
    {
        var candidate = new PodcastSearchResult
        {
            Id = "listennotes:pod-1",
            Name = "AI Show",
            Description = "AI content",
            FeedUrl = "https://example.com/feed.xml",
            EstimatedReach = 0.5
        };

        var payloads = new Dictionary<string, RssParsePayload>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://example.com/feed.xml"] = new RssParsePayload
            {
                PodcastEmail = "host@example.com",
                PodcastLanguage = "en",
                Episodes = episodes
            }
        };

        return BuildPipeline(now, [candidate], payloads);
    }

    private static IReadOnlyList<Episode> BuildRecentEpisodes(DateTimeOffset now, string t1, string t2, string t3)
    {
        return
        [
            new Episode(t1, t1, now.AddDays(-5)),
            new Episode(t2, t2, now.AddDays(-12)),
            new Episode(t3, t3, now.AddDays(-19))
        ];
    }

    [Fact]
    public async Task RunAsync_FiltersByLanguage_UsingRssTagThenHeuristics()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new PodcastSearchResult
            {
                Id = "podchaser:en-tag",
                Name = "English Feed",
                Description = "English language show.",
                FeedUrl = "https://feeds.example.com/en-tag.xml",
                EstimatedReach = 0.6
            },
            new PodcastSearchResult
            {
                Id = "podchaser:fr-tag",
                Name = "French Feed",
                Description = "French language show.",
                FeedUrl = "https://feeds.example.com/fr-tag.xml",
                EstimatedReach = 0.9
            },
            new PodcastSearchResult
            {
                Id = "podchaser:hu-heuristic",
                Name = "Magyar Beszelgetes",
                Description = "Magyar interju es beszelgetes tema.",
                FeedUrl = "https://feeds.example.com/hu-heuristic.xml",
                EstimatedReach = 0.5
            },
            new PodcastSearchResult
            {
                Id = "podchaser:es-heuristic",
                Name = "Charlas en Espanol",
                Description = "Contenido en espanol sobre tecnologia.",
                FeedUrl = "https://feeds.example.com/es-heuristic.xml",
                EstimatedReach = 0.95
            }
        };

        var payloads = new Dictionary<string, RssParsePayload>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://feeds.example.com/en-tag.xml"] = new RssParsePayload
            {
                PodcastEmail = "en@example.com",
                PodcastLanguage = "en-US",
                Episodes = BuildRecentEpisodes(now, "Health episode", "Science episode", "Interview episode")
            },
            ["https://feeds.example.com/fr-tag.xml"] = new RssParsePayload
            {
                PodcastEmail = "fr@example.com",
                PodcastLanguage = "fr-FR",
                Episodes = BuildRecentEpisodes(now, "Episode un", "Episode deux", "Episode trois")
            },
            ["https://feeds.example.com/hu-heuristic.xml"] = new RssParsePayload
            {
                PodcastEmail = "hu@example.com",
                PodcastLanguage = null,
                Episodes = BuildRecentEpisodes(now, "Magyar interju", "Beszelgetes es tema", "Egeszseg es eletmod")
            },
            ["https://feeds.example.com/es-heuristic.xml"] = new RssParsePayload
            {
                PodcastEmail = "es@example.com",
                PodcastLanguage = null,
                Episodes = BuildRecentEpisodes(now, "Charlas", "Tecnologia", "Noticias")
            }
        };

        var pipeline = BuildPipeline(now, candidates, payloads);
        var result = await pipeline.RunAsync(
            new CliArguments(["health"], PublishedAfterDays: 60, Top: 10, JsonOutput: false, PrettyJson: false));
        var showIds = result.Results.Select(r => r.ShowId).ToArray();

        Assert.Contains("podchaser:en-tag", showIds);
        Assert.Contains("podchaser:hu-heuristic", showIds);
        Assert.DoesNotContain("podchaser:fr-tag", showIds);
        Assert.DoesNotContain("podchaser:es-heuristic", showIds);
    }

    private sealed class StubPodcastSearchClient(IReadOnlyList<PodcastSearchResult> candidates) : IPodcastSearchClient
    {
        private readonly IReadOnlyList<PodcastSearchResult> _candidates = candidates;

        public Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(
            IReadOnlyList<string> keywords,
            int publishedAfterDays,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_candidates);
        }
    }

    private sealed class StubRssFeedClient(
        IReadOnlyDictionary<string, RssParsePayload> payloads,
        IReadOnlyDictionary<string, HttpStatusCode>? failures) : IRssFeedClient
    {
        private readonly IReadOnlyDictionary<string, RssParsePayload> _payloads = payloads;
        private readonly IReadOnlyDictionary<string, HttpStatusCode> _failures = failures ?? new Dictionary<string, HttpStatusCode>(StringComparer.OrdinalIgnoreCase);

        public Task<string> GetFeedXmlAsync(string feedUrl, CancellationToken cancellationToken = default)
        {
            if (_failures.TryGetValue(feedUrl, out var statusCode))
            {
                throw new HttpRequestException($"Simulated HTTP {(int)statusCode}", null, statusCode);
            }

            if (!_payloads.ContainsKey(feedUrl))
            {
                throw new HttpRequestException("Feed not found", null, HttpStatusCode.NotFound);
            }

            // Encodes the feed URL so the parser can map to deterministic fixture payloads.
            return Task.FromResult(feedUrl);
        }
    }

    private sealed class StubRssParser(IReadOnlyDictionary<string, RssParsePayload> payloads) : IRssParser
    {
        private readonly IReadOnlyDictionary<string, RssParsePayload> _payloads = payloads;

        public Task<Result<RssParsePayload>> ParseAsync(string feedXml, CancellationToken cancellationToken = default)
        {
            if (!_payloads.TryGetValue(feedXml, out var payload))
            {
                return Task.FromResult(Result<RssParsePayload>.Fail(new RssParseIssue("missing_payload", "Missing payload fixture.")));
            }

            return Task.FromResult(Result<RssParsePayload>.Ok(new RssParsePayload
            {
                PodcastEmail = payload.PodcastEmail,
                PodcastLanguage = payload.PodcastLanguage,
                Episodes = payload.Episodes
            }));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
