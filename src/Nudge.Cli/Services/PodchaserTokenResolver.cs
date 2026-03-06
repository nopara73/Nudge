using Nudge.Cli.Models;

namespace Nudge.Cli.Services;

public static class PodchaserTokenResolver
{
    public static IReadOnlyList<PodchaserResolvedToken> ResolveLabeled(PodchaserOptions options)
    {
        var tokens = new List<PodchaserResolvedToken>();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);

        AddToken(tokens, seenValues, "primary", options.Token);
        AddTokens(tokens, seenValues, "fallback", options.FallbackTokens);

        return tokens.ToArray();
    }

    public static IReadOnlyList<string> Resolve(PodchaserOptions options)
    {
        return ResolveLabeled(options)
            .Select(static token => token.Value)
            .ToArray();
    }

    private static void AddToken(
        ICollection<PodchaserResolvedToken> tokens,
        ISet<string> seenValues,
        string label,
        string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            var trimmed = token.Trim();
            if (seenValues.Add(trimmed))
            {
                tokens.Add(new PodchaserResolvedToken(label, trimmed));
            }
        }
    }

    private static void AddTokens(
        ICollection<PodchaserResolvedToken> tokens,
        ISet<string> seenValues,
        string labelPrefix,
        IEnumerable<string>? values)
    {
        if (values is null)
        {
            return;
        }

        var index = 1;
        foreach (var value in values)
        {
            var beforeCount = tokens.Count;
            AddToken(tokens, seenValues, $"{labelPrefix}-{index}", value);
            if (tokens.Count > beforeCount)
            {
                index++;
            }
        }
    }
}

public sealed record PodchaserResolvedToken(string Label, string Value);
