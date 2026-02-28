namespace Nudge.Cli.Services;

public static class PodcastSearchClientModeResolver
{
    public static bool? TryParseUseMockValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var parsed))
        {
            return parsed;
        }

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }

    public static (bool UseMock, bool MissingApiKeyWarning) ResolveUseMock(
        bool cliUseMock,
        bool? envUseMock,
        string? apiKey)
    {
        var useMockOverride = envUseMock ?? cliUseMock;
        if (useMockOverride)
        {
            return (true, false);
        }

        var apiKeyMissingOrInvalid = string.IsNullOrWhiteSpace(apiKey) || !LooksLikeBearerToken(apiKey);
        return apiKeyMissingOrInvalid
            ? (true, true)
            : (false, false);
    }

    private static bool LooksLikeBearerToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var firstDot = trimmed.IndexOf('.');
        if (firstDot <= 0)
        {
            return false;
        }

        var secondDot = trimmed.IndexOf('.', firstDot + 1);
        return secondDot > firstDot + 1 && secondDot < trimmed.Length - 1;
    }
}
