using Nudge.Cli.Models;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public static class CliArgumentParser
{
    public static Result<CliArguments> TryParse(string[] args)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        var json = false;
        var pretty = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
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

        var keywordsRaw = named.TryGetValue("--keywords", out var k) ? k : positional.FirstOrDefault() ?? string.Empty;
        var daysRaw = named.TryGetValue("--published-after-days", out var d) ? d : positional.Skip(1).FirstOrDefault() ?? "30";
        var topRaw = named.TryGetValue("--top", out var t) ? t : "10";

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

        var keywords = keywordsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        return Result<CliArguments>.Ok(new CliArguments(keywords, days, top, json, pretty));
    }
}
