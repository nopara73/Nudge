using Nudge.Cli.Models;
using Nudge.Core.Models;
using System.Globalization;

namespace Nudge.Cli.Services;

public static class CliArgumentParser
{
    public static Result<CliArguments> TryParse(string[] args)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        var json = false;
        var pretty = false;
        var skipHardToReachOnes = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                if (arg.Equals("--use-mock", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        i++;
                    }

                    continue;
                }

                if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase))
                {
                    json = true;
                    continue;
                }

                if (arg.Equals("--pretty", StringComparison.OrdinalIgnoreCase))
                {
                    pretty = true;
                    continue;
                }

                if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (arg.Equals("--skiphardtoreachones", StringComparison.OrdinalIgnoreCase))
                {
                    skipHardToReachOnes = true;
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    return Result<CliArguments>.Fail(
                        new RssParseIssue("missing_value", $"Missing value for option '{arg}'."));
                }

                named[arg] = args[i + 1];
                i++;
                continue;
            }

            positional.Add(arg);
        }

        var searchTermsRaw = named.TryGetValue("--search-terms", out var searchTermsValue) ? searchTermsValue : string.Empty;
        var keywordsRaw = named.TryGetValue("--keywords", out var k) ? k : positional.FirstOrDefault() ?? string.Empty;
        var daysRaw = named.TryGetValue("--published-after-days", out var d) ? d : positional.Skip(1).FirstOrDefault() ?? "30";
        var topRaw = named.TryGetValue("--top", out var t) ? t : "3";
        var minReachRaw = named.TryGetValue("--min-reach", out var minReachValue) ? minReachValue : null;
        var maxReachRaw = named.TryGetValue("--max-reach", out var maxReachValue) ? maxReachValue : null;

        if (!int.TryParse(daysRaw, out var days) || days < 0)
        {
            return Result<CliArguments>.Fail(
                new RssParseIssue("invalid_days", "published_after_days must be a non-negative integer."));
        }

        if (!int.TryParse(topRaw, out var top) || top <= 0)
        {
            return Result<CliArguments>.Fail(
                new RssParseIssue("invalid_top", "top must be a positive integer."));
        }

        if (!TryParseReachBound(minReachRaw, "min_reach", out var minReach, out var minReachError))
        {
            return Result<CliArguments>.Fail(minReachError!);
        }

        if (!TryParseReachBound(maxReachRaw, "max_reach", out var maxReach, out var maxReachError))
        {
            return Result<CliArguments>.Fail(maxReachError!);
        }

        if (minReach.HasValue && maxReach.HasValue && minReach.Value > maxReach.Value)
        {
            return Result<CliArguments>.Fail(
                new RssParseIssue("invalid_reach_bounds", "min_reach must be less than or equal to max_reach."));
        }

        var searchTerms = searchTermsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        var keywords = keywordsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        if (searchTerms.Length == 0)
        {
            return Result<CliArguments>.Fail(
                new RssParseIssue("missing_search_terms", "search_terms must include at least one comma-separated value."));
        }

        return Result<CliArguments>.Ok(new CliArguments(searchTerms, keywords, days, top, json, pretty, minReach, maxReach, skipHardToReachOnes));
    }

    private static bool TryParseReachBound(string? rawValue, string codeSuffix, out double? parsedValue, out RssParseIssue? error)
    {
        parsedValue = null;
        error = null;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 1)
        {
            error = new RssParseIssue($"invalid_{codeSuffix}", $"{codeSuffix} must be a number between 0.0 and 1.0.");
            return false;
        }

        parsedValue = value;
        return true;
    }
}
