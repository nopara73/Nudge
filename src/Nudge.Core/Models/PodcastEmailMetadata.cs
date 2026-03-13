namespace Nudge.Core.Models;

public static class PodcastEmailSources
{
    public const string Unknown = "unknown";
    public const string ItunesEmail = "itunes_email";
    public const string ItunesOwnerEmail = "itunes_owner_email";
    public const string DescriptionRegex = "description_regex";

    public static bool IsLowConfidence(string? source)
    {
        return string.Equals(source, DescriptionRegex, StringComparison.Ordinal);
    }
}

public readonly record struct PodcastEmailResolution(string? Email, string? Source);
