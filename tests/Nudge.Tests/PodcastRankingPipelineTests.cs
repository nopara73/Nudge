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
                Name = "Masters Athlete Performance Lab",
                Description = "Focused athlete training competition show.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/niche.xml",
                EstimatedReach = 0.62
            },
            new PodcastSearchResult
            {
                Id = "podchaser:generic-fitness",
                Name = "Daily Fitness Roundup",
                Description = "General fitness headlines.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/generic.xml",
                EstimatedReach = 0.95
            },
            new PodcastSearchResult
            {
                Id = "podchaser:tie-a",
                Name = "Wellness Weekly",
                Description = "General wellness updates.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/tie-a.xml",
                EstimatedReach = 0.55
            },
            new PodcastSearchResult
            {
                Id = "podchaser:tie-b",
                Name = "Wellness Weekly",
                Description = "General wellness updates.",
                Language = "en",
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
                    "Athlete competition training block",
                    "Crossfit PR ranking review",
                    "VO2 strength masters prep")
            },
            ["https://feeds.example.com/generic.xml"] = new RssParsePayload
            {
                PodcastEmail = "host@generic.example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(
                    now,
                    "Top gadgets this week",
                    "Routine myths",
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
        Assert.NotNull(run1.Results[0].NicheFitBreakdown);
        Assert.Contains(run1.Warnings, w => w.Contains("Missing contact email penalty applied", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_IntentAwareNicheScoring_PrioritizesAthleteAndPenalizesBusiness()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new PodcastSearchResult
            {
                Id = "podchaser:athlete",
                Name = "Masters Performance Longevity",
                Description = "Athlete training and competition prep.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/athlete.xml",
                EstimatedReach = 0.65
            },
            new PodcastSearchResult
            {
                Id = "podchaser:longevity",
                Name = "Longevity Science Journal",
                Description = "Aging and healthspan research.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/longevity.xml",
                EstimatedReach = 0.65
            },
            new PodcastSearchResult
            {
                Id = "podchaser:business",
                Name = "Wellness Business Builder",
                Description = "Revenue and marketing playbooks for coaching entrepreneurs.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/business.xml",
                EstimatedReach = 0.65
            }
        };

        var payloads = new Dictionary<string, RssParsePayload>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://feeds.example.com/athlete.xml"] = new RssParsePayload
            {
                PodcastEmail = "athlete@example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(
                    now,
                    "Training for competition",
                    "VO2 and strength block",
                    "Crossfit PR and ranking")
            },
            ["https://feeds.example.com/longevity.xml"] = new RssParsePayload
            {
                PodcastEmail = "longevity@example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(
                    now,
                    "Aging pathways",
                    "Longevity evidence",
                    "Healthspan biomarkers")
            },
            ["https://feeds.example.com/business.xml"] = new RssParsePayload
            {
                PodcastEmail = "business@example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(
                    now,
                    "Marketing for fitness coaches",
                    "Revenue and sales systems",
                    "Wellness business growth")
            }
        };

        var pipeline = BuildPipeline(now, candidates, payloads);
        var args = new CliArguments(["longevity", "fitness"], PublishedAfterDays: 60, Top: 10, JsonOutput: false, PrettyJson: false);

        var run1 = await pipeline.RunAsync(args);
        var run2 = await pipeline.RunAsync(args);

        Assert.Equal(run1.Results.Select(r => r.ShowId), run2.Results.Select(r => r.ShowId));
        Assert.Equal("podchaser:athlete", run1.Results[0].ShowId);

        var longevity = run1.Results.Single(r => r.ShowId == "podchaser:longevity");
        var business = run1.Results.Single(r => r.ShowId == "podchaser:business");
        Assert.True(longevity.NicheFit > business.NicheFit);
        Assert.Contains(business.NicheFitBreakdown!.TokenHits, t => t.Token == "revenue" && t.Weight < 0);
    }

    [Fact]
    public async Task RunAsync_ActivityScore_DemotesPodfadedHighNiche_BelowRecentModerateNiche()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new PodcastSearchResult
            {
                Id = "podchaser:old-high-niche",
                Name = "Elite Athlete Competition Lab",
                Description = "Masters crossfit hyrox training performance podcast.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/old-high-niche.xml",
                EstimatedReach = 0.72
            },
            new PodcastSearchResult
            {
                Id = "podchaser:recent-moderate",
                Name = "Longevity Fitness Weekly",
                Description = "General longevity and fitness updates.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/recent-moderate.xml",
                EstimatedReach = 0.72
            }
        };

        var payloads = new Dictionary<string, RssParsePayload>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://feeds.example.com/old-high-niche.xml"] = new RssParsePayload
            {
                PodcastEmail = "old@example.com",
                PodcastLanguage = "en",
                Episodes =
                [
                    new Episode("Competition training PR ranking", "Athlete VO2 strength", now.AddDays(-240)),
                    new Episode("Crossfit hyrox masters prep", "Performance deep dive", now.AddDays(-260)),
                    new Episode("Athlete training systems", "Ranking and PR strategy", now.AddDays(-290))
                ]
            },
            ["https://feeds.example.com/recent-moderate.xml"] = new RssParsePayload
            {
                PodcastEmail = "recent@example.com",
                PodcastLanguage = "en",
                Episodes = BuildRecentEpisodes(
                    now,
                    "Longevity basics",
                    "Fitness habits",
                    "Aging and healthspan")
            }
        };

        var pipeline = BuildPipeline(now, candidates, payloads);
        var result = await pipeline.RunAsync(
            new CliArguments(["longevity", "fitness"], PublishedAfterDays: 365, Top: 10, JsonOutput: false, PrettyJson: false));

        Assert.Equal("podchaser:recent-moderate", result.Results[0].ShowId);
        var oldHighNiche = result.Results.Single(r => r.ShowId == "podchaser:old-high-niche");
        var recentModerate = result.Results.Single(r => r.ShowId == "podchaser:recent-moderate");
        Assert.True(oldHighNiche.NicheFit > recentModerate.NicheFit);
        Assert.True(oldHighNiche.ActivityScore < recentModerate.ActivityScore);
        Assert.True(oldHighNiche.Score < recentModerate.Score);
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
                Language = "en",
                FeedUrl = "https://feeds.example.com/good.xml",
                EstimatedReach = 0.75
            },
            new PodcastSearchResult
            {
                Id = "podchaser:not-found",
                Name = "Broken 404 Feed",
                Description = "Unavailable feed.",
                Language = "en",
                FeedUrl = "https://feeds.example.com/404.xml",
                EstimatedReach = 0.9
            },
            new PodcastSearchResult
            {
                Id = "podchaser:server-error",
                Name = "Broken 500 Feed",
                Description = "Unavailable feed.",
                Language = "en",
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
            Language = "en",
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
    public async Task RunAsync_FiltersByLanguage_UsingApiMetadataThenHeuristics()
    {
        var now = new DateTimeOffset(2026, 2, 28, 0, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            new PodcastSearchResult
            {
                Id = "podchaser:en-tag",
                Name = "English Feed",
                Description = "English language show.",
                Language = "en-US",
                FeedUrl = "https://feeds.example.com/en-tag.xml",
                EstimatedReach = 0.6
            },
            new PodcastSearchResult
            {
                Id = "podchaser:fr-tag",
                Name = "French Feed",
                Description = "French language show.",
                Language = "fr-FR",
                FeedUrl = "https://feeds.example.com/fr-tag.xml",
                EstimatedReach = 0.9
            },
            new PodcastSearchResult
            {
                Id = "podchaser:hu-heuristic",
                Name = "Magyar Beszelgetes",
                Description = "Magyar interju es beszelgetes tema.",
                Language = null,
                FeedUrl = "https://feeds.example.com/hu-heuristic.xml",
                EstimatedReach = 0.5
            },
            new PodcastSearchResult
            {
                Id = "podchaser:es-heuristic",
                Name = "Charlas en Espanol",
                Description = "Contenido en espanol sobre tecnologia.",
                Language = null,
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
        Assert.Equal("en", result.Results.Single(r => r.ShowId == "podchaser:en-tag").DetectedLanguage);
        Assert.Equal("hu", result.Results.Single(r => r.ShowId == "podchaser:hu-heuristic").DetectedLanguage);
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
