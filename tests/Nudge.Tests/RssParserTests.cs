using Nudge.Cli.Services;

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
                <itunes:owner>
                  <itunes:email>owner@example.com</itunes:email>
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
        Assert.Equal("owner@example.com", result.Payload!.PodcastEmail);
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
}
