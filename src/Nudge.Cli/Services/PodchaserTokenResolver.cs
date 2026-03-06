using Nudge.Cli.Models;

namespace Nudge.Cli.Services;

public static class PodchaserTokenResolver
{
    public static IReadOnlyList<string> Resolve(PodchaserOptions options)
    {
        var tokens = new List<string>();

        AddToken(tokens, options.Token);
        AddTokens(tokens, options.FallbackTokens);

        return tokens
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddToken(ICollection<string> tokens, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            tokens.Add(token.Trim());
        }
    }

    private static void AddTokens(ICollection<string> tokens, IEnumerable<string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var value in values)
        {
            AddToken(tokens, value);
        }
    }
}
