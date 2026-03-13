using Nudge.Cli.Services;
using Nudge.Core.Models;

namespace Nudge.Tests;

public sealed class RssParserTests
{
    [Fact]
    public async Task ParseAsync_UsesOwnerEmailFallback_WhenChannelEmailMissing()
    {
        const string xml =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Owner Fallback Podcast</title>
                <language>en-us</language>
                <itunes:owner>
                  <itunes:email>owner@example.com</itunes:email>
                </itunes:owner>
                <item>
                  <title>Episode 1</title>
                  <description>Desc</description>
                  <pubDate>Fri, 20 Feb 2026 10:00:00 GMT</pubDate>
                  <link>https://example.com/episodes/1</link>
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("owner@example.com", result.Payload!.PodcastEmail);
        Assert.Equal(PodcastEmailSources.ItunesOwnerEmail, result.Payload.PodcastEmailSource);
        Assert.Equal("en-us", result.Payload.PodcastLanguage);
        Assert.Equal("https://example.com/episodes/1", result.Payload.Episodes[0].Url);
    }

    [Fact]
    public async Task ParseAsync_UsesRegexFallbackWithDeobfuscation_AndChoosesFirstMatch()
    {
        const string xml =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Regex Fallback Podcast</title>
                <itunes:email>   </itunes:email>
                <description>General contact: team [at] example.com</description>
                <item>
                  <title>Episode 1</title>
                  <description>Sponsor inquiries at ads(at)example.org and backup@example.net</description>
                  <pubDate>Fri, 20 Feb 2026 10:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("team@example.com", result.Payload!.PodcastEmail);
        Assert.Equal(PodcastEmailSources.DescriptionRegex, result.Payload.PodcastEmailSource);
    }

    [Fact]
    public async Task ParseAsync_NormalizesDatesToUtc_AndCapturesInvalidDateIssues()
    {
        const string xml =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Date Parsing Podcast</title>
                <item>
                  <title>Recent Episode</title>
                  <description>Valid timezone date.</description>
                  <pubDate>Wed, 18 Feb 2026 08:00:00 -0500</pubDate>
                </item>
                <item>
                  <title>Broken Date Episode</title>
                  <description>Invalid date format.</description>
                  <pubDate>Definitely Invalid</pubDate>
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(new DateTimeOffset(2026, 2, 18, 13, 0, 0, TimeSpan.Zero), result.Payload!.Episodes[0].PublishedAtUtc);
        Assert.Contains(result.Errors, issue => issue.Code == "invalid_pub_date");
    }

    [Fact]
    public async Task ParseAsync_ExtractsSingleAndMultipleHosts_FromCommonFields()
    {
        const string xml =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Host Parsing Podcast</title>
                <itunes:author>Jane Doe &amp; John Roe</itunes:author>
                <itunes:owner>
                  <itunes:name>Jane Doe</itunes:name>
                </itunes:owner>
                <item>
                  <title>Episode 1</title>
                  <description>Desc</description>
                  <pubDate>Fri, 20 Feb 2026 10:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(["Jane Doe", "John Roe"], result.Payload!.PodcastHosts);
    }

    [Fact]
    public async Task ParseAsync_InferHostFromTitle_WhenAuthorAndOwnerMissing()
    {
        const string xml =
            """
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
              <channel>
                <title>Super Strength Show with Ray Toulany | Interviews with Health and Fitness Leaders</title>
                <itunes:author></itunes:author>
                <itunes:owner>
                  <itunes:name></itunes:name>
                </itunes:owner>
                <item>
                  <title>Episode 1</title>
                  <description>Desc</description>
                  <pubDate>Fri, 20 Feb 2026 10:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal(["Ray Toulany"], result.Payload!.PodcastHosts);
    }

    [Fact]
    public async Task ParseAsync_ExtractsTranscriptAndAudioUrls_FromEpisodeElements()
    {
        const string xml =
            """
            <rss version="2.0" xmlns:podcast="https://podcastindex.org/namespace/1.0">
              <channel>
                <title>Transcript Podcast</title>
                <item>
                  <title>Episode 1</title>
                  <description>Desc</description>
                  <pubDate>Fri, 20 Feb 2026 10:00:00 GMT</pubDate>
                  <enclosure url="https://example.com/audio/1.mp3" type="audio/mpeg" />
                  <podcast:transcript url="https://example.com/transcripts/1.txt" type="text/plain" />
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        var episode = Assert.Single(result.Payload!.Episodes);
        Assert.Equal("https://example.com/audio/1.mp3", episode.AudioUrl);
        Assert.Equal("https://example.com/transcripts/1.txt", episode.TranscriptUrl);
    }

    [Fact]
    public async Task ParseAsync_UsesGuidPermalink_WhenLinkElementMissing()
    {
        const string xml =
            """
            <rss version="2.0">
              <channel>
                <title>Guid Link Podcast</title>
                <item>
                  <title>Episode 1</title>
                  <guid isPermaLink="true">https://example.com/episodes/1</guid>
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        var episode = Assert.Single(result.Payload!.Episodes);
        Assert.Equal("https://example.com/episodes/1", episode.Url);
    }

    [Fact]
    public async Task ParseAsync_UsesAtomLinkHref_WhenRssLinkAndGuidMissing()
    {
        const string xml =
            """
            <rss version="2.0" xmlns:atom="http://www.w3.org/2005/Atom">
              <channel>
                <title>Atom Link Podcast</title>
                <item>
                  <title>Episode 1</title>
                  <atom:link href="https://example.com/episodes/atom-1" rel="alternate" />
                </item>
              </channel>
            </rss>
            """;

        var parser = new RssParser();
        var result = await parser.ParseAsync(xml);

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        var episode = Assert.Single(result.Payload!.Episodes);
        Assert.Equal("https://example.com/episodes/atom-1", episode.Url);
    }
}
