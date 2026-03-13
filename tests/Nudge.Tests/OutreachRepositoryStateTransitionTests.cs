using System.Net;
using System.Net.Http;
using System.Text;
using Nudge.Core.Models;
using Nudge.Ui.Models;
using Nudge.Ui.Services;

namespace Nudge.Tests;

[Collection("OutreachRepositoryState")]
public sealed class OutreachRepositoryStateTransitionTests
{
    [Fact]
    public async Task ContactedCooldown_Expires_AndRemainsContacted()
    {
        using var databaseScope = new RepositoryDatabaseScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new OutreachRepository(time, databaseScope.DatabasePath);

        var item = await SeedSingleTargetAsync(repository, time.GetUtcNow());
        await repository.MarkContactedAsync(item, string.Empty, string.Empty);

        var afterContacted = Assert.Single(await repository.GetTrackerItemsAsync());
        Assert.Equal(OutreachState.ContactedWaiting, afterContacted.State);
        Assert.NotNull(afterContacted.CooldownUntilUtc);

        time.Advance(TimeSpan.FromDays(91));

        var afterCooldownExpiry = Assert.Single(await repository.GetTrackerItemsAsync());
        Assert.Equal(OutreachState.ContactedWaiting, afterCooldownExpiry.State);
        Assert.Null(afterCooldownExpiry.CooldownUntilUtc);
    }

    [Fact]
    public async Task RepliedYes_GetsThirtyDayFollowup_ThenBecomesActionable()
    {
        using var databaseScope = new RepositoryDatabaseScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new OutreachRepository(time, databaseScope.DatabasePath);

        var item = await SeedSingleTargetAsync(repository, time.GetUtcNow());
        await repository.MarkRepliedYesAsync(item, string.Empty, string.Empty);

        var afterRepliedYes = Assert.Single(await repository.GetTrackerItemsAsync());
        Assert.Equal(OutreachState.RepliedYes, afterRepliedYes.State);
        Assert.NotNull(afterRepliedYes.CooldownUntilUtc);
        Assert.Equal(time.GetUtcNow().AddDays(30), afterRepliedYes.CooldownUntilUtc!.Value);

        time.Advance(TimeSpan.FromDays(31));

        var afterFollowupExpiry = Assert.Single(await repository.GetTrackerItemsAsync());
        Assert.Equal(OutreachState.RepliedYes, afterFollowupExpiry.State);
        Assert.Null(afterFollowupExpiry.CooldownUntilUtc);
    }

    [Fact]
    public async Task InterviewDone_IsTerminalAndHasNoCooldown()
    {
        using var databaseScope = new RepositoryDatabaseScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new OutreachRepository(time, databaseScope.DatabasePath);

        var item = await SeedSingleTargetAsync(repository, time.GetUtcNow());
        await repository.MarkInterviewDoneAsync(item, string.Empty, string.Empty);

        time.Advance(TimeSpan.FromDays(365));

        var afterLongTime = Assert.Single(await repository.GetTrackerItemsAsync());
        Assert.Equal(OutreachState.InterviewDone, afterLongTime.State);
        Assert.Null(afterLongTime.CooldownUntilUtc);
    }

    [Fact]
    public async Task SaveRunAsync_RoundTripsContactEmailSource_ToQueueItems()
    {
        using var databaseScope = new RepositoryDatabaseScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new OutreachRepository(time, databaseScope.DatabasePath);

        var item = await SeedSingleTargetAsync(repository, time.GetUtcNow(), PodcastEmailSources.DescriptionRegex);

        Assert.Equal(PodcastEmailSources.DescriptionRegex, item.ContactEmailSource);
    }

    [Fact]
    public async Task GetTrackerItemsAsync_BackfillsMissingContactEmailSource_FromSavedFeedUrl()
    {
        using var databaseScope = new RepositoryDatabaseScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        using var httpClient = new HttpClient(new StubHttpMessageHandler(
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Longevity Lab</title>
                <description>Reach the team at host [at] example.com</description>
                <item>
                  <title>Episode 1</title>
                  <description>Desc</description>
                </item>
              </channel>
            </rss>
            """))
        {
            BaseAddress = new Uri("https://example.com/")
        };
        var repository = new OutreachRepository(time, databaseScope.DatabasePath, httpClient);

        var envelope = new CliOutputEnvelope
        {
            GeneratedAtUtc = time.GetUtcNow(),
            Total = 1,
            Results =
            [
                new CliOutputResultItem
                {
                    ShowId = "show-1",
                    ShowName = "Longevity Lab",
                    DetectedLanguage = "en",
                    FeedUrl = "https://example.com/feed.xml",
                    ContactEmail = "host@example.com",
                    Reach = 0.7,
                    Frequency = 0.6,
                    NicheFit = 0.8,
                    ActivityScore = 0.9,
                    OutreachPriority = "High",
                    Score = 0.82,
                    NewestEpisodePublishedAtUtc = time.GetUtcNow(),
                    NicheFitBreakdown = new { tokenHits = Array.Empty<object>() }
                }
            ]
        };

        await repository.SaveRunAsync(envelope, "test-command", string.Empty, string.Empty);
        var item = Assert.Single(await repository.GetTrackerItemsAsync());

        Assert.Equal(PodcastEmailSources.DescriptionRegex, item.ContactEmailSource);
    }

    private static async Task<QueueItem> SeedSingleTargetAsync(OutreachRepository repository, DateTimeOffset nowUtc, string? contactEmailSource = null)
    {
        var envelope = new CliOutputEnvelope
        {
            GeneratedAtUtc = nowUtc,
            Total = 1,
            Results =
            [
                new CliOutputResultItem
                {
                    ShowId = "show-1",
                    ShowName = "Longevity Lab",
                    DetectedLanguage = "en",
                    FeedUrl = "https://example.com/feed.xml",
                    ContactEmail = "host@example.com",
                    ContactEmailSource = contactEmailSource,
                    Reach = 0.7,
                    Frequency = 0.6,
                    NicheFit = 0.8,
                    ActivityScore = 0.9,
                    OutreachPriority = "High",
                    Score = 0.82,
                    NewestEpisodePublishedAtUtc = nowUtc,
                    NicheFitBreakdown = new { tokenHits = Array.Empty<object>() }
                }
            ]
        };

        await repository.SaveRunAsync(envelope, "test-command", string.Empty, string.Empty);
        return Assert.Single(await repository.GetTrackerItemsAsync());
    }

    private sealed class MutableTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        private DateTimeOffset _nowUtc = nowUtc;

        public override DateTimeOffset GetUtcNow() => _nowUtc;

        public void Advance(TimeSpan delta) => _nowUtc = _nowUtc.Add(delta);
    }

    private sealed class RepositoryDatabaseScope : IDisposable
    {
        private readonly string _tempRoot;
        public string DatabasePath { get; }

        public RepositoryDatabaseScope()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), $"Nudge.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempRoot);
            DatabasePath = Path.Combine(_tempRoot, "nudge-ui.db");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup for temp test data.
            }
        }
    }

    private sealed class StubHttpMessageHandler(string responseBody) : HttpMessageHandler
    {
        private readonly string _responseBody = responseBody;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/rss+xml")
            });
        }
    }
}

[CollectionDefinition("OutreachRepositoryState", DisableParallelization = true)]
public sealed class OutreachRepositoryStateCollection;
