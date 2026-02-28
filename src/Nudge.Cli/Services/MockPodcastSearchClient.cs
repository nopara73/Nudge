using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class MockPodcastSearchClient : IPodcastSearchClient
{
    private static readonly IReadOnlyList<PodcastSearchResult> SeededPodcasts =
    [
        new PodcastSearchResult
        {
            Id = "show-ai-founders",
            Name = "AI Founders Weekly",
            Description = "Interviews with startup founders building AI products.",
            FeedUrl = "memory://ai-founders",
            EstimatedReach = 0.76
        },
        new PodcastSearchResult
        {
            Id = "show-b2b-growth",
            Name = "B2B Growth Stories",
            Description = "Practical growth playbooks for SaaS and B2B marketing teams.",
            FeedUrl = "memory://b2b-growth",
            EstimatedReach = 0.63
        },
        new PodcastSearchResult
        {
            Id = "show-creator-playbook",
            Name = "Creator Monetization Playbook",
            Description = "How creators build audiences, monetize newsletters, and scale podcasts.",
            FeedUrl = "memory://creator-playbook",
            EstimatedReach = 0.58
        }
    ];

    public Task<IReadOnlyList<PodcastSearchResult>> SearchAsync(
        IReadOnlyList<string> keywords,
        int publishedAfterDays,
        CancellationToken cancellationToken = default)
    {
        var normalizedKeywords = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        if (normalizedKeywords.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<PodcastSearchResult>>(SeededPodcasts);
        }

        var filtered = SeededPodcasts
            .Where(show =>
            {
                var corpus = $"{show.Name} {show.Description}".ToLowerInvariant();
                return normalizedKeywords.Any(corpus.Contains);
            })
            .ToArray();

        return Task.FromResult<IReadOnlyList<PodcastSearchResult>>(filtered);
    }

    public static IReadOnlyDictionary<string, string> SeededFeeds { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["memory://ai-founders"] =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>AI Founders Weekly</title>
                <itunes:owner>
                  <itunes:name>AI Founders Team</itunes:name>
                  <itunes:email>team@aifoundersweekly.fm</itunes:email>
                </itunes:owner>
                <description>Reach us at partnerships [at] aifoundersweekly.fm for collaborations.</description>
                <item>
                  <title>How AI copilots change onboarding</title>
                  <description>Discussing SaaS onboarding and startup growth loops.</description>
                  <pubDate>Fri, 20 Feb 2026 10:00:00 GMT</pubDate>
                </item>
                <item>
                  <title>Building moats with workflow AI</title>
                  <description>Founder interview on defensibility in B2B AI.</description>
                  <pubDate>Fri, 13 Feb 2026 10:00:00 GMT</pubDate>
                </item>
                <item>
                  <title>Lessons from failed launches</title>
                  <description>Email us via contact(at)aifoundersweekly.fm.</description>
                  <pubDate>Fri, 06 Feb 2026 10:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """,
        ["memory://b2b-growth"] =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>B2B Growth Stories</title>
                <itunes:email>editor@b2bgrowthstories.com</itunes:email>
                <description>Stories from B2B marketers scaling revenue.</description>
                <item>
                  <title>Category design for technical products</title>
                  <description>Positioning and narrative with examples.</description>
                  <pubDate>Wed, 18 Feb 2026 08:00:00 -0500</pubDate>
                </item>
                <item>
                  <title>Sales and marketing handoff</title>
                  <description>How revenue teams coordinate better.</description>
                  <pubDate>Wed, 04 Feb 2026 08:00:00 -0500</pubDate>
                </item>
                <item>
                  <title>Demand capture vs demand creation</title>
                  <description>Budgeting across channels and cycles.</description>
                  <pubDate>Invalid Date Example</pubDate>
                </item>
              </channel>
            </rss>
            """,
        ["memory://creator-playbook"] =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Creator Monetization Playbook</title>
                <description>Business systems for creator-led brands. Partnerships at hello (at) creatorplaybook.io.</description>
                <item>
                  <title>Newsletter funnels that convert</title>
                  <description>From content to product with practical examples.</description>
                  <pubDate>Mon, 12 Jan 2026 15:00:00 GMT</pubDate>
                </item>
                <item>
                  <title>Sponsorship pricing fundamentals</title>
                  <description>How audience quality affects ad rates.</description>
                  <pubDate>Mon, 22 Dec 2025 15:00:00 GMT</pubDate>
                </item>
                <item>
                  <title>Membership retention playbook</title>
                  <description>Retention tactics and community loops.</description>
                  <pubDate>Mon, 01 Dec 2025 15:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """
    };
}
