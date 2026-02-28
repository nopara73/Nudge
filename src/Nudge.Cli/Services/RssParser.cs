using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed partial class RssParser : IRssParser
{
    private static readonly XNamespace ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    public Task<Result<RssParsePayload>> ParseAsync(string feedXml, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedXml))
        {
            return Task.FromResult(Result<RssParsePayload>.Fail(new RssParseIssue("empty_feed", "RSS feed XML is empty.")));
        }

        var issues = new List<RssParseIssue>();

        try
        {
            var document = XDocument.Parse(feedXml, LoadOptions.None);
            var channel = document.Root?.Element("channel");
            if (channel is null)
            {
                return Task.FromResult(Result<RssParsePayload>.Fail(new RssParseIssue("invalid_feed", "RSS channel node was not found.")));
            }

            var email = ExtractEmail(channel);
            var episodes = ParseEpisodes(channel, issues);
            var payload = new RssParsePayload
            {
                PodcastEmail = email,
                Episodes = episodes
            };

            return Task.FromResult(Result<RssParsePayload>.Ok(payload, issues));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result<RssParsePayload>.Fail(new RssParseIssue("parse_exception", ex.Message)));
        }
    }

    private static IReadOnlyList<Episode> ParseEpisodes(XElement channel, List<RssParseIssue> issues)
    {
        var feedOrderEpisodes = new List<(Episode Episode, int FeedOrder)>();
        var itemElements = channel.Elements("item").ToList();

        for (var index = 0; index < itemElements.Count; index++)
        {
            var item = itemElements[index];
            var title = item.Element("title")?.Value?.Trim() ?? string.Empty;
            var description = item.Element("description")?.Value?.Trim() ?? string.Empty;
            var rawPubDate = item.Element("pubDate")?.Value?.Trim();

            DateTimeOffset? publishedAtUtc = null;
            if (!string.IsNullOrWhiteSpace(rawPubDate))
            {
                if (DateTimeOffset.TryParse(rawPubDate, out var parsedDate))
                {
                    publishedAtUtc = parsedDate.ToUniversalTime();
                }
                else
                {
                    issues.Add(new RssParseIssue("invalid_pub_date", $"Unable to parse pubDate '{rawPubDate}' for episode '{title}'."));
                }
            }

            feedOrderEpisodes.Add((new Episode(title, description, publishedAtUtc, rawPubDate), index));
        }

        return feedOrderEpisodes
            .OrderByDescending(e => e.Episode.PublishedAtUtc.HasValue)
            .ThenByDescending(e => e.Episode.PublishedAtUtc)
            .ThenBy(e => e.FeedOrder)
            .Take(3)
            .Select(e => e.Episode)
            .ToArray();
    }

    private static string? ExtractEmail(XElement channel)
    {
        // Explicitly query iTunes namespace elements as required.
        var channelItunesEmail = channel.Element(ItunesNs + "email")?.Value;
        var ownerEmail = channel.Element(ItunesNs + "owner")?.Element(ItunesNs + "email")?.Value;
        var candidate = FirstNonEmpty(channelItunesEmail, ownerEmail);
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            return candidate.Trim();
        }

        var textToSearch = string.Join(
            ' ',
            channel.Elements("description").Select(e => e.Value).Concat(
                channel.Elements("item").SelectMany(i => i.Elements("description").Select(d => d.Value))));
        var normalizedText = NormalizeObfuscatedEmailPatterns(textToSearch);
        var matches = EmailRegex().Matches(normalizedText);
        if (matches.Count == 0)
        {
            return null;
        }

        // Deterministically return the first valid match.
        return matches[0].Value;
    }

    private static string NormalizeObfuscatedEmailPatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var output = input;
        output = output.Replace("[at]", "@", StringComparison.OrdinalIgnoreCase);
        output = output.Replace("(at)", "@", StringComparison.OrdinalIgnoreCase);
        output = AtWordRegex().Replace(output, "@");
        output = AroundAtWhitespaceRegex().Replace(output, "@");
        return output;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    [GeneratedRegex(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\bat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AtWordRegex();

    [GeneratedRegex(@"\s*@\s*", RegexOptions.Compiled)]
    private static partial Regex AroundAtWhitespaceRegex();
}
