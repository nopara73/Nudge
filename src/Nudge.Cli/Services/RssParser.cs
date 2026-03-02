using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed partial class RssParser : IRssParser
{
    private static readonly XNamespace ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";
    private const int RecentEpisodeWindow = 7;
    private static readonly string[] HostSeparators = [",", "&", " and ", " with ", "|", "/", ";"];

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
            var language = ExtractLanguage(channel);
            var hosts = ExtractHosts(channel);
            var episodes = ParseEpisodes(channel, issues);
            var payload = new RssParsePayload
            {
                PodcastEmail = email,
                PodcastLanguage = language,
                PodcastHosts = hosts,
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
            var link = ExtractEpisodeUrl(item);
            var audioUrl = item.Element("enclosure")?.Attribute("url")?.Value?.Trim();
            var transcriptUrl = ExtractTranscriptUrl(item);

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

            feedOrderEpisodes.Add((new Episode(title, description, publishedAtUtc, rawPubDate, link, audioUrl, transcriptUrl), index));
        }

        return feedOrderEpisodes
            .OrderByDescending(e => e.Episode.PublishedAtUtc.HasValue)
            .ThenByDescending(e => e.Episode.PublishedAtUtc)
            .ThenBy(e => e.FeedOrder)
            .Take(RecentEpisodeWindow)
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

    private static string? ExtractLanguage(XElement channel)
    {
        var rawLanguage = channel.Element("language")?.Value;
        if (string.IsNullOrWhiteSpace(rawLanguage))
        {
            return null;
        }

        return rawLanguage.Trim();
    }

    private static IReadOnlyList<string> ExtractHosts(XElement channel)
    {
        var candidates = new List<string?>();
        candidates.Add(channel.Element(ItunesNs + "author")?.Value);
        candidates.Add(channel.Element("author")?.Value);
        candidates.Add(channel.Element(ItunesNs + "owner")?.Element(ItunesNs + "name")?.Value);

        var hosts = new List<string>();
        foreach (var rawCandidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(rawCandidate))
            {
                continue;
            }

            foreach (var parsedHost in SplitHostNames(rawCandidate))
            {
                if (hosts.Any(existing => string.Equals(existing, parsedHost, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                hosts.Add(parsedHost);
            }
        }

        if (hosts.Count > 0)
        {
            return hosts;
        }

        return InferHostsFromChannelTitle(channel);
    }

    private static IReadOnlyList<string> InferHostsFromChannelTitle(XElement channel)
    {
        var title = channel.Element("title")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<string>();
        }

        var match = TitleWithHostsRegex().Match(title);
        if (!match.Success)
        {
            return Array.Empty<string>();
        }

        var rawHosts = match.Groups["hosts"].Value?.Trim();
        if (string.IsNullOrWhiteSpace(rawHosts))
        {
            return Array.Empty<string>();
        }

        return SplitHostNames(rawHosts)
            .Where(IsLikelyPersonName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLikelyPersonName(string value)
    {
        var tokens = value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.All(character => char.IsLetter(character) || character is '\'' or '-'))
            .ToArray();
        if (tokens.Length is < 2 or > 4)
        {
            return false;
        }

        return tokens.All(token => token.Length >= 2 && char.IsUpper(token[0]));
    }

    [GeneratedRegex(@"\bwith\s+(?<hosts>[^|:\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TitleWithHostsRegex();

    private static IReadOnlyList<string> SplitHostNames(string rawValue)
    {
        var normalized = rawValue.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        var hostNames = new List<string> { normalized };
        foreach (var separator in HostSeparators)
        {
            hostNames = hostNames
                .SelectMany(name => name.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .ToList();
        }

        return NormalizeHostNames(hostNames);
    }

    private static IReadOnlyList<string> NormalizeHostNames(IEnumerable<string> hostNames) =>
        hostNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? ExtractTranscriptUrl(XElement item)
    {
        var transcriptElement = item
            .Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "transcript", StringComparison.OrdinalIgnoreCase));
        if (transcriptElement is null)
        {
            return null;
        }

        var url = transcriptElement.Attribute("url")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        var value = transcriptElement.Value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? ExtractEpisodeUrl(XElement item)
    {
        var rssLink = item.Element("link")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(rssLink))
        {
            return rssLink;
        }

        var guid = item.Element("guid");
        var guidValue = guid?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(guidValue) &&
            Uri.TryCreate(guidValue, UriKind.Absolute, out _))
        {
            var isPermaLink = guid?.Attribute("isPermaLink")?.Value;
            if (string.IsNullOrWhiteSpace(isPermaLink) ||
                bool.TryParse(isPermaLink, out var parsed) && parsed)
            {
                return guidValue;
            }
        }

        var atomLinkHref = item
            .Elements(AtomNs + "link")
            .Select(element => element.Attribute("href")?.Value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return string.IsNullOrWhiteSpace(atomLinkHref) ? null : atomLinkHref;
    }

    [GeneratedRegex(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\bat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AtWordRegex();

    [GeneratedRegex(@"\s*@\s*", RegexOptions.Compiled)]
    private static partial Regex AroundAtWhitespaceRegex();
}
