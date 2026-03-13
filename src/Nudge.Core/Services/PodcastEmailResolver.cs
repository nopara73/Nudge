using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nudge.Core.Models;

namespace Nudge.Core.Services;

public static partial class PodcastEmailResolver
{
    private static readonly XNamespace ItunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";

    public static PodcastEmailResolution ResolveFromFeedXml(string feedXml)
    {
        if (string.IsNullOrWhiteSpace(feedXml))
        {
            return default;
        }

        try
        {
            var document = XDocument.Parse(feedXml, LoadOptions.None);
            var channel = document.Root?.Element("channel");
            return channel is null ? default : ResolveFromChannel(channel);
        }
        catch
        {
            return default;
        }
    }

    public static PodcastEmailResolution ResolveFromChannel(XElement channel)
    {
        var channelItunesEmail = channel.Element(ItunesNs + "email")?.Value;
        if (!string.IsNullOrWhiteSpace(channelItunesEmail))
        {
            return new PodcastEmailResolution(channelItunesEmail.Trim(), PodcastEmailSources.ItunesEmail);
        }

        var ownerEmail = channel.Element(ItunesNs + "owner")?.Element(ItunesNs + "email")?.Value;
        if (!string.IsNullOrWhiteSpace(ownerEmail))
        {
            return new PodcastEmailResolution(ownerEmail.Trim(), PodcastEmailSources.ItunesOwnerEmail);
        }

        var textToSearch = string.Join(
            ' ',
            channel.Elements("description").Select(e => e.Value).Concat(
                channel.Elements("item").SelectMany(i => i.Elements("description").Select(d => d.Value))));
        var normalizedText = NormalizeObfuscatedEmailPatterns(textToSearch);
        var matches = EmailRegex().Matches(normalizedText);
        if (matches.Count == 0)
        {
            return default;
        }

        return new PodcastEmailResolution(matches[0].Value, PodcastEmailSources.DescriptionRegex);
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

    [GeneratedRegex(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\bat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AtWordRegex();

    [GeneratedRegex(@"\s*@\s*", RegexOptions.Compiled)]
    private static partial Regex AroundAtWhitespaceRegex();
}
