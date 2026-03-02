using Nudge.Ui.Models;
using Nudge.Ui.Services;

namespace Nudge.Tests;

[Collection("OutreachRepositoryState")]
public sealed class OutreachRepositoryStateTransitionTests
{
    [Fact]
    public async Task ContactedCooldown_Expires_AndRemainsContacted()
    {
        using var appDataScope = new LocalAppDataScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new OutreachRepository(time);

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
        using var appDataScope = new LocalAppDataScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new OutreachRepository(time);

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
        using var appDataScope = new LocalAppDataScope();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var repository = new OutreachRepository(time);

        var item = await SeedSingleTargetAsync(repository, time.GetUtcNow());
        await repository.MarkInterviewDoneAsync(item, string.Empty, string.Empty);

        time.Advance(TimeSpan.FromDays(365));

        var afterLongTime = Assert.Single(await repository.GetTrackerItemsAsync());
        Assert.Equal(OutreachState.InterviewDone, afterLongTime.State);
        Assert.Null(afterLongTime.CooldownUntilUtc);
    }

    private static async Task<QueueItem> SeedSingleTargetAsync(OutreachRepository repository, DateTimeOffset nowUtc)
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

    private sealed class LocalAppDataScope : IDisposable
    {
        private readonly string? _original;
        private readonly string _tempRoot;

        public LocalAppDataScope()
        {
            _original = Environment.GetEnvironmentVariable("LOCALAPPDATA", EnvironmentVariableTarget.Process);
            _tempRoot = Path.Combine(Path.GetTempPath(), $"Nudge.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempRoot);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _tempRoot, EnvironmentVariableTarget.Process);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", _original, EnvironmentVariableTarget.Process);
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
}

[CollectionDefinition("OutreachRepositoryState", DisableParallelization = true)]
public sealed class OutreachRepositoryStateCollection;
