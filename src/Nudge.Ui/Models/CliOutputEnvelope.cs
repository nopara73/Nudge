using System.Text.Json.Serialization;

namespace Nudge.Ui.Models;

public sealed class CliOutputEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "1.0";

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; init; }

    [JsonPropertyName("arguments")]
    public CliOutputArguments Arguments { get; init; } = new();

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("results")]
    public List<CliOutputResultItem> Results { get; init; } = [];

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; init; } = [];
}

public sealed class CliOutputArguments
{
    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; init; } = [];

    [JsonPropertyName("publishedAfterDays")]
    public int PublishedAfterDays { get; init; }

    [JsonPropertyName("top")]
    public int Top { get; init; }
}

public sealed class CliOutputResultItem
{
    [JsonPropertyName("showId")]
    public string ShowId { get; init; } = string.Empty;

    [JsonPropertyName("showName")]
    public string ShowName { get; init; } = string.Empty;

    [JsonPropertyName("detectedLanguage")]
    public string DetectedLanguage { get; init; } = string.Empty;

    [JsonPropertyName("feedUrl")]
    public string FeedUrl { get; init; } = string.Empty;

    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; init; }

    [JsonPropertyName("reach")]
    public double Reach { get; init; }

    [JsonPropertyName("frequency")]
    public double Frequency { get; init; }

    [JsonPropertyName("nicheFit")]
    public double NicheFit { get; init; }

    [JsonPropertyName("activityScore")]
    public double ActivityScore { get; init; }

    [JsonPropertyName("outreachPriority")]
    public string OutreachPriority { get; init; } = "Low";

    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("newestEpisodePublishedAtUtc")]
    public DateTimeOffset? NewestEpisodePublishedAtUtc { get; init; }

    [JsonPropertyName("recentEpisodeTitles")]
    public List<string> RecentEpisodeTitles { get; init; } = [];

    [JsonPropertyName("nicheFitBreakdown")]
    public object? NicheFitBreakdown { get; init; }
}
