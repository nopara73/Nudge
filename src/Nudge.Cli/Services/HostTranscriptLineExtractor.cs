using System.Text.RegularExpressions;
using System.Net;

namespace Nudge.Cli.Services;

public sealed partial class HostTranscriptLineExtractor : IHostTranscriptLineExtractor
{
    public IReadOnlyList<string> ExtractHostLines(string? transcript, IReadOnlyList<string> podcastHosts)
    {
        if (string.IsNullOrWhiteSpace(transcript) || podcastHosts.Count == 0)
        {
            return Array.Empty<string>();
        }

        var hostAliases = BuildHostAliases(podcastHosts);
        if (hostAliases.Count == 0)
        {
            return Array.Empty<string>();
        }

        var htmlHostLines = ExtractHostLinesFromHtmlTranscript(transcript, hostAliases);
        if (htmlHostLines.Count > 0)
        {
            return htmlHostLines;
        }

        var hostLines = new List<string>();
        var currentSpeakerIsHost = false;
        foreach (var rawLine in transcript.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseSpeakerLine(rawLine, out var speaker, out var content))
            {
                if (currentSpeakerIsHost && !string.IsNullOrWhiteSpace(rawLine))
                {
                    hostLines.Add(rawLine.Trim());
                }

                continue;
            }

            currentSpeakerIsHost = IsHostSpeaker(speaker, hostAliases);
            if (!currentSpeakerIsHost)
            {
                continue;
            }

            var cleanedContent = NormalizeContent(content);
            if (!string.IsNullOrWhiteSpace(cleanedContent))
            {
                hostLines.Add(cleanedContent);
            }
        }

        return hostLines;
    }

    private static IReadOnlyList<string> ExtractHostLinesFromHtmlTranscript(string transcript, HashSet<string> hostAliases)
    {
        var decodedTranscript = WebUtility.HtmlDecode(transcript);
        var speakerSections = HtmlSpeakerSectionRegex().Matches(decodedTranscript);
        if (speakerSections.Count == 0)
        {
            return Array.Empty<string>();
        }

        var hostLines = new List<string>();
        foreach (Match sectionMatch in speakerSections)
        {
            var speaker = WebUtility.HtmlDecode(sectionMatch.Groups["speaker"].Value);
            if (!IsHostSpeaker(speaker, hostAliases))
            {
                continue;
            }

            var section = sectionMatch.Groups["section"].Value;
            var paragraphMatches = HtmlParagraphRegex().Matches(section);
            if (paragraphMatches.Count == 0)
            {
                var fallback = NormalizeHtmlText(section);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    hostLines.Add(fallback);
                }

                continue;
            }

            foreach (Match paragraphMatch in paragraphMatches)
            {
                var cleanedContent = NormalizeHtmlText(paragraphMatch.Groups["content"].Value);
                if (!string.IsNullOrWhiteSpace(cleanedContent))
                {
                    hostLines.Add(cleanedContent);
                }
            }
        }

        return hostLines;
    }

    private static string NormalizeHtmlText(string content)
    {
        var decodedContent = WebUtility.HtmlDecode(content);
        var tagStripped = HtmlTagRegex().Replace(decodedContent, " ");
        var normalizedWhitespace = WhitespaceRegex().Replace(tagStripped, " ").Trim();
        return NormalizeContent(normalizedWhitespace);
    }

    private static HashSet<string> BuildHostAliases(IReadOnlyList<string> podcastHosts)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in podcastHosts)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var normalizedHost = NormalizeSpeaker(host);
            if (!string.IsNullOrWhiteSpace(normalizedHost))
            {
                aliases.Add(normalizedHost);
            }

            foreach (var token in TokenRegex().Matches(host).Select(match => match.Value))
            {
                var normalizedToken = NormalizeSpeaker(token);
                if (normalizedToken.Length >= 3)
                {
                    aliases.Add(normalizedToken);
                }
            }
        }

        return aliases;
    }

    private static bool TryParseSpeakerLine(string line, out string speaker, out string content)
    {
        speaker = string.Empty;
        content = string.Empty;

        var bracketed = BracketedSpeakerRegex().Match(line);
        if (bracketed.Success)
        {
            speaker = bracketed.Groups["speaker"].Value;
            content = bracketed.Groups["content"].Value;
            return true;
        }

        var delimited = DelimitedSpeakerRegex().Match(line);
        if (delimited.Success)
        {
            speaker = delimited.Groups["speaker"].Value;
            content = delimited.Groups["content"].Value;
            return true;
        }

        return false;
    }

    private static bool IsHostSpeaker(string speaker, HashSet<string> hostAliases)
    {
        var normalizedSpeaker = NormalizeSpeaker(speaker);
        return !string.IsNullOrWhiteSpace(normalizedSpeaker) && hostAliases.Contains(normalizedSpeaker);
    }

    private static string NormalizeContent(string content)
    {
        var trimmed = content.Trim().Trim('*').Trim();
        var timestampMatch = LeadingTimestampRegex().Match(trimmed);
        if (!timestampMatch.Success)
        {
            return trimmed;
        }

        var remainder = timestampMatch.Groups["rest"].Value.Trim();
        return remainder;
    }

    private static string NormalizeSpeaker(string value)
    {
        return string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    [GeneratedRegex(@"\[(?<speaker>[^\]]+)\]\s*[:\-]?\s*(?<content>.+)$", RegexOptions.Compiled)]
    private static partial Regex BracketedSpeakerRegex();

    [GeneratedRegex(@"^(?<speaker>[^:\-]{1,50})\s*[:\-]\s*(?<content>.+)$", RegexOptions.Compiled)]
    private static partial Regex DelimitedSpeakerRegex();

    [GeneratedRegex(@"^(?:\d{1,2}:)?\d{1,2}:\d{2}(?:\s*[-–—:]\s*)?(?<rest>.*)$", RegexOptions.Compiled)]
    private static partial Regex LeadingTimestampRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"<cite\b[^>]*>\s*(?<speaker>.*?)\s*</cite>(?<section>.*?)(?=<cite\b[^>]*>|$)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlSpeakerSectionRegex();

    [GeneratedRegex(@"<p\b[^>]*>\s*(?<content>.*?)\s*</p>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlParagraphRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
