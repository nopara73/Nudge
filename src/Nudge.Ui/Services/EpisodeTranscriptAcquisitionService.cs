using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;
using Nudge.Ui.Models;

namespace Nudge.Ui.Services;

public sealed partial class EpisodeTranscriptAcquisitionService
{
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";
    private const string UserAgent = "Nudge-Ui-Transcript/1.0";
    private readonly HttpClient _httpClient;
    private readonly ILocalEpisodeSttTranscriber _sttTranscriber;
    private readonly ConcurrentDictionary<string, string> _downloadedTranscriptCache = new(StringComparer.OrdinalIgnoreCase);

    public EpisodeTranscriptAcquisitionService(HttpClient httpClient, ILocalEpisodeSttTranscriber sttTranscriber)
    {
        _httpClient = httpClient;
        _sttTranscriber = sttTranscriber;
    }

    public async Task<TranscriptViewContent?> AcquireAsync(
        QueueEpisode episode,
        IReadOnlyList<string> podcastHosts,
        string? feedUrl,
        bool hostOnly,
        Action<string>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        reportProgress?.Invoke("Checking published transcript...");
        var transcriptText = await ResolveTranscriptTextAsync(episode, cancellationToken);
        FeedEpisodeMetadata? feedMetadata = null;
        if (string.IsNullOrWhiteSpace(transcriptText) && !string.IsNullOrWhiteSpace(feedUrl))
        {
            reportProgress?.Invoke("Scanning podcast feed for transcript metadata...");
            feedMetadata = await DiscoverEpisodeMetadataFromFeedAsync(feedUrl, episode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(feedMetadata?.TranscriptUrl))
            {
                reportProgress?.Invoke("Downloading transcript from feed...");
                transcriptText = await DownloadTranscriptTextAsync(feedMetadata.TranscriptUrl!, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(transcriptText) && !string.IsNullOrWhiteSpace(feedMetadata?.ShowNotesFallback))
            {
                reportProgress?.Invoke("No transcript URL in feed. Using published show notes as fallback text...");
                transcriptText = feedMetadata.ShowNotesFallback;
            }
        }

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            var episodeUrl = !string.IsNullOrWhiteSpace(episode.Url)
                ? episode.Url
                : feedMetadata?.EpisodeUrl;
            if (!string.IsNullOrWhiteSpace(episodeUrl))
            {
                reportProgress?.Invoke("No published transcript found. Probing episode page for transcript content...");
                transcriptText = await TryResolveTranscriptFromEpisodePageAsync(episodeUrl, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            var audioUrl = !string.IsNullOrWhiteSpace(episode.AudioUrl)
                ? episode.AudioUrl
                : feedMetadata?.AudioUrl;
            if (!string.IsNullOrWhiteSpace(audioUrl))
            {
                reportProgress?.Invoke("No transcript published. Running automatic transcription...");
                transcriptText = await _sttTranscriber.TranscribeFromAudioUrlAsync(audioUrl, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(transcriptText))
        {
            return null;
        }

        var transcriptBody = transcriptText.Trim();
        if (!hostOnly)
        {
            return new TranscriptViewContent(
                $"{episode.Title} - Transcript",
                transcriptBody,
                "full transcript");
        }

        var hostLines = episode.HostTranscriptLines.Count > 0
            ? episode.HostTranscriptLines
            : ExtractHostLines(transcriptBody, podcastHosts);
        if (hostLines.Count > 0)
        {
            return new TranscriptViewContent(
                $"{episode.Title} - Host Lines",
                string.Join(Environment.NewLine + Environment.NewLine, hostLines),
                "host-only lines");
        }

        var inferredSpeaker = InferPrimarySpeakerLines(transcriptBody);
        if (inferredSpeaker.Lines.Count > 0)
        {
            return new TranscriptViewContent(
                $"{episode.Title} - Host Lines (Inferred)",
                string.Join(Environment.NewLine + Environment.NewLine, inferredSpeaker.Lines),
                $"host-only lines (inferred: {inferredSpeaker.Speaker})");
        }

        return new TranscriptViewContent(
            $"{episode.Title} - Host Lines (Fallback)",
            "[No speaker labels detected. Showing full transcript instead.]\n\n" + transcriptBody,
            "host-only fallback (full transcript)");
    }

    private async Task<string?> ResolveTranscriptTextAsync(QueueEpisode episode, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(episode.Transcript))
        {
            return episode.Transcript;
        }

        if (string.IsNullOrWhiteSpace(episode.TranscriptUrl))
        {
            return null;
        }

        return await DownloadTranscriptTextAsync(episode.TranscriptUrl, cancellationToken);
    }

    private async Task<string?> DownloadTranscriptTextAsync(string transcriptUrl, CancellationToken cancellationToken)
    {
        if (_downloadedTranscriptCache.TryGetValue(transcriptUrl, out var cachedTranscript))
        {
            return cachedTranscript;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, transcriptUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var downloadedTranscript = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(downloadedTranscript))
            {
                return null;
            }

            var normalized = downloadedTranscript.Trim();
            _downloadedTranscriptCache.TryAdd(transcriptUrl, normalized);
            return normalized;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private async Task<FeedEpisodeMetadata?> DiscoverEpisodeMetadataFromFeedAsync(
        string feedUrl,
        QueueEpisode episode,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, feedUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var feedXml = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(feedXml))
            {
                return null;
            }

            var document = XDocument.Parse(feedXml, LoadOptions.None);
            var channel = document.Root?.Element("channel");
            if (channel is null)
            {
                return null;
            }

            var matchingItem = channel
                .Elements("item")
                .FirstOrDefault(item => IsMatchingEpisode(item, episode));
            if (matchingItem is null)
            {
                return null;
            }

            return new FeedEpisodeMetadata(
                ExtractTranscriptUrl(matchingItem),
                ExtractEpisodeUrl(matchingItem),
                matchingItem.Element("enclosure")?.Attribute("url")?.Value?.Trim(),
                ExtractShowNotesFallback(matchingItem));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsMatchingEpisode(XElement item, QueueEpisode episode)
    {
        if (!string.IsNullOrWhiteSpace(episode.Url))
        {
            var itemLink = ExtractEpisodeUrl(item);
            if (string.Equals(itemLink, episode.Url, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var itemTitle = item.Element("title")?.Value?.Trim();
        return !string.IsNullOrWhiteSpace(itemTitle) &&
               string.Equals(itemTitle, episode.Title, StringComparison.OrdinalIgnoreCase);
    }

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

    private static string? ExtractShowNotesFallback(XElement item)
    {
        var contentEncoded = item
            .Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, "encoded", StringComparison.OrdinalIgnoreCase))
            ?.Value;
        var description = item.Element("description")?.Value;

        foreach (var candidate in new[] { contentEncoded, description })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = NormalizeHtmlText(candidate);
            if (normalized.Length >= 160)
            {
                return normalized;
            }
        }

        return null;
    }

    private async Task<string?> TryResolveTranscriptFromEpisodePageAsync(string episodeUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(episodeUrl, UriKind.Absolute, out var pageUri))
        {
            return null;
        }

        var html = await DownloadTextAsync(pageUri, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var fromJson = TryExtractTranscriptFromJsonLd(html);
        if (!string.IsNullOrWhiteSpace(fromJson))
        {
            return fromJson;
        }

        var fromLinkedResource = await TryExtractTranscriptFromLinkedResourcesAsync(pageUri, html, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fromLinkedResource))
        {
            return fromLinkedResource;
        }

        return TryExtractTranscriptSectionFromHtml(html);
    }

    private async Task<string?> TryExtractTranscriptFromLinkedResourcesAsync(
        Uri pageUri,
        string html,
        CancellationToken cancellationToken)
    {
        var candidates = AnchorHrefRegex()
            .Matches(html)
            .Select(match => BuildTranscriptLinkCandidate(match))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .DistinctBy(candidate => candidate.AbsoluteUri)
            .Take(5)
            .ToArray();

        foreach (var candidate in candidates)
        {
            var body = await DownloadTextAsync(candidate, cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            var normalized = NormalizeLinkedTranscriptBody(candidate, body);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;

        Uri? BuildTranscriptLinkCandidate(Match match)
        {
            var hrefRaw = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (string.IsNullOrWhiteSpace(hrefRaw))
            {
                return null;
            }

            var innerText = NormalizeHtmlText(match.Groups["inner"].Value);
            var hrefHint = hrefRaw.ToLowerInvariant();
            var textHint = innerText.ToLowerInvariant();
            var likelyTranscript =
                hrefHint.Contains("transcript", StringComparison.Ordinal) ||
                hrefHint.EndsWith(".vtt", StringComparison.Ordinal) ||
                hrefHint.EndsWith(".srt", StringComparison.Ordinal) ||
                hrefHint.EndsWith(".txt", StringComparison.Ordinal) ||
                textHint.Contains("transcript", StringComparison.Ordinal) ||
                textHint.Contains("captions", StringComparison.Ordinal) ||
                textHint.Contains("subtitles", StringComparison.Ordinal);
            if (!likelyTranscript)
            {
                return null;
            }

            return Uri.TryCreate(pageUri, hrefRaw, out var resolved) ? resolved : null;
        }
    }

    private async Task<string?> DownloadTextAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(body) ? null : body;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractTranscriptFromJsonLd(string html)
    {
        foreach (Match match in JsonLdScriptRegex().Matches(html))
        {
            var content = WebUtility.HtmlDecode(match.Groups["content"].Value).Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            try
            {
                using var json = JsonDocument.Parse(content);
                var candidate = ExtractLikelyTranscriptFromJsonElement(json.RootElement);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
            catch (JsonException)
            {
                // Continue scanning other blocks.
            }
        }

        return null;
    }

    private static string? ExtractLikelyTranscriptFromJsonElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    string? articleBodyFallback = null;
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var text = property.Value.GetString()?.Trim();
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                continue;
                            }

                            if (property.NameEquals("transcript"))
                            {
                                return NormalizeWhitespace(text);
                            }

                            if (property.NameEquals("articleBody") && text.Length >= 600)
                            {
                                articleBodyFallback = NormalizeWhitespace(text);
                            }
                        }

                        var nested = ExtractLikelyTranscriptFromJsonElement(property.Value);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            return nested;
                        }
                    }

                    return articleBodyFallback;
                }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = ExtractLikelyTranscriptFromJsonElement(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }

                break;
        }

        return null;
    }

    private static string? TryExtractTranscriptSectionFromHtml(string html)
    {
        var transcriptHeading = TranscriptHeadingSectionRegex().Match(html);
        if (!transcriptHeading.Success)
        {
            return null;
        }

        var section = transcriptHeading.Groups["section"].Value;
        var normalized = NormalizeHtmlText(section);
        return normalized.Length >= 80 ? normalized : null;
    }

    private static string? NormalizeLinkedTranscriptBody(Uri sourceUri, string body)
    {
        var lowerPath = sourceUri.AbsolutePath.ToLowerInvariant();
        if (lowerPath.EndsWith(".vtt", StringComparison.Ordinal))
        {
            return NormalizeTimedText(body, VttLineRegex());
        }

        if (lowerPath.EndsWith(".srt", StringComparison.Ordinal))
        {
            return NormalizeTimedText(body, SrtTimeRangeRegex());
        }

        if (LooksLikeHtml(body))
        {
            var section = TryExtractTranscriptSectionFromHtml(body);
            if (!string.IsNullOrWhiteSpace(section))
            {
                return section;
            }

            var normalizedHtml = NormalizeHtmlText(body);
            return normalizedHtml.Length >= 600 ? normalizedHtml : null;
        }

        var normalizedText = NormalizeWhitespace(WebUtility.HtmlDecode(body));
        return normalizedText.Length >= 200 ? normalizedText : null;
    }

    private static string NormalizeTimedText(string body, Regex timestampRegex)
    {
        var lines = body
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase) &&
                !timestampRegex.IsMatch(line) &&
                !int.TryParse(line, out _))
            .Select(line => line.Trim())
            .ToArray();

        return NormalizeWhitespace(string.Join(' ', lines));
    }

    private static bool LooksLikeHtml(string value)
    {
        return value.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("<p", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("<div", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string value)
    {
        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    private static IReadOnlyList<string> ExtractHostLines(string transcript, IReadOnlyList<string> podcastHosts)
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

            currentSpeakerIsHost = hostAliases.Contains(NormalizeSpeaker(speaker));
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
            if (!hostAliases.Contains(NormalizeSpeaker(speaker)))
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

    private static HashSet<string> BuildHostAliases(IReadOnlyList<string> hosts)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var host in hosts)
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

    private static string NormalizeSpeaker(string value)
    {
        return string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    private static string NormalizeContent(string content)
    {
        var trimmed = content.Trim().Trim('*').Trim();
        var timestampMatch = LeadingTimestampRegex().Match(trimmed);
        if (!timestampMatch.Success)
        {
            return trimmed;
        }

        return timestampMatch.Groups["rest"].Value.Trim();
    }

    private static (string Speaker, IReadOnlyList<string> Lines) InferPrimarySpeakerLines(string transcript)
    {
        var bySpeaker = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in transcript.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryParseSpeakerLine(rawLine, out var speaker, out var content) || string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var key = speaker.Trim();
            if (!bySpeaker.TryGetValue(key, out var lines))
            {
                lines = [];
                bySpeaker[key] = lines;
            }

            lines.Add(content.Trim());
        }

        if (bySpeaker.Count == 0)
        {
            return (string.Empty, Array.Empty<string>());
        }

        var winner = bySpeaker
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .First();
        return (winner.Key, winner.Value);
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

    [GeneratedRegex(@"<script\b[^>]*type\s*=\s*[""']application\/ld\+json[""'][^>]*>\s*(?<content>.*?)\s*</script>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex JsonLdScriptRegex();

    [GeneratedRegex(@"<a\b[^>]*href\s*=\s*[""'](?<href>[^""'#>]+)[""'][^>]*>(?<inner>.*?)</a>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AnchorHrefRegex();

    [GeneratedRegex(@"<h[1-6]\b[^>]*>\s*(?:episode\s+)?transcript\b.*?</h[1-6]>(?<section>.*?)(?=<h[1-6]\b|$)", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TranscriptHeadingSectionRegex();

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}\.\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}\.\d{3}", RegexOptions.Compiled)]
    private static partial Regex VttLineRegex();

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2},\d{3}\s*-->\s*\d{2}:\d{2}:\d{2},\d{3}", RegexOptions.Compiled)]
    private static partial Regex SrtTimeRangeRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    private sealed record FeedEpisodeMetadata(
        string? TranscriptUrl,
        string? EpisodeUrl,
        string? AudioUrl,
        string? ShowNotesFallback);
}
