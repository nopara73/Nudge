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

        var apiKeyMissing = string.IsNullOrWhiteSpace(apiKey);
        return apiKeyMissing
            ? (true, true)
            : (false, false);
    }
}
