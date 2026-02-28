namespace Nudge.Core.Models;

public sealed record Episode(
    string Title,
    string Description,
    DateTimeOffset? PublishedAtUtc,
    string? RawPublishedDate = null
);
