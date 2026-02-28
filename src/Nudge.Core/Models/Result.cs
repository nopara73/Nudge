namespace Nudge.Core.Models;

public sealed record Result<T>
{
    public bool Success { get; init; }
    public T? Payload { get; init; }
    public IReadOnlyList<RssParseIssue> Errors { get; init; } = Array.Empty<RssParseIssue>();

    public static Result<T> Ok(T payload, IReadOnlyList<RssParseIssue>? errors = null) =>
        new()
        {
            Success = true,
            Payload = payload,
            Errors = errors ?? Array.Empty<RssParseIssue>()
        };

    public static Result<T> Fail(params RssParseIssue[] errors) =>
        new()
        {
            Success = false,
            Payload = default,
            Errors = errors
        };
}
