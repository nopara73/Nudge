using Nudge.Cli.Models;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class PodcastRankingPipeline(
    IPodcastSearchClient searchClient,
    IRssFeedClient feedClient,
    IRssParser rssParser,
    IScoringService scoringService,
    TimeProvider timeProvider)
{
    private readonly IPodcastSearchClient _searchClient = searchClient;
    private readonly IRssFeedClient _feedClient = feedClient;
    private readonly IRssParser _rssParser = rssParser;
    private readonly IScoringService _scoringService = scoringService;
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<RankingRunResult> RunAsync(CliArguments arguments, CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var candidates = await _searchClient.SearchAsync(arguments.Keywords, arguments.PublishedAfterDays, cancellationToken);
        var thresholdUtc = _timeProvider.GetUtcNow().AddDays(-arguments.PublishedAfterDays);

        var ranked = new List<RankedTarget>();
        using var semaphore = new SemaphoreSlim(5, 5);
        var tasks = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var rankedTarget = await BuildRankedTargetAsync(candidate, arguments.Keywords, thresholdUtc, cancellationToken);
                if (rankedTarget is not null)
                {
                    lock (ranked)
                    {
                        ranked.Add(rankedTarget);
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lock (warnings)
                {
                    warnings.Add($"Skipped '{candidate.Name}' after retry: {ex.Message}");
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var ordered = ranked
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.NewestEpisodePublishedAtUtc)
            .ThenBy(r => r.ShowName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RankingRunResult
        {
            Results = ordered,
            Warnings = warnings
        };
    }

    private async Task<RankedTarget?> BuildRankedTargetAsync(
        PodcastSearchResult candidate,
        IReadOnlyList<string> keywords,
        DateTimeOffset thresholdUtc,
        CancellationToken cancellationToken)
    {
        var xml = await _feedClient.GetFeedXmlAsync(candidate.FeedUrl, cancellationToken);
        var parseResult = await _rssParser.ParseAsync(xml, cancellationToken);
        if (!parseResult.Success || parseResult.Payload is null)
        {
            return null;
        }

        var eligibleEpisodes = parseResult.Payload.Episodes
            .Where(e => e.PublishedAtUtc.HasValue && e.PublishedAtUtc.Value >= thresholdUtc)
            .ToArray();

        if (eligibleEpisodes.Length == 0)
        {
            return null;
        }

        var show = new Show
        {
            Id = candidate.Id,
            Name = candidate.Name,
            Description = candidate.Description,
            FeedUrl = candidate.FeedUrl,
            EstimatedReach = candidate.EstimatedReach,
            ContactMethod = string.IsNullOrWhiteSpace(parseResult.Payload.PodcastEmail) ? ContactMethod.None : ContactMethod.Email,
            ContactValue = parseResult.Payload.PodcastEmail,
            Episodes = eligibleEpisodes
        };

        var intent = _scoringService.Score(show, keywords);
        return new RankedTarget
        {
            ShowId = show.Id,
            ShowName = show.Name,
            FeedUrl = show.FeedUrl,
            ContactEmail = show.ContactValue,
            Reach = intent.Reach,
            Frequency = intent.Frequency,
            NicheFit = intent.NicheFit,
            Score = intent.Score,
            NewestEpisodePublishedAtUtc = intent.NewestEpisodePublishedAtUtc,
            RecentEpisodeTitles = eligibleEpisodes.Select(e => e.Title).ToArray()
        };
    }
}
