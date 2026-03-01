using System.Text.Json;
using Nudge.Ui.Models;

namespace Nudge.Ui.Services;

public sealed class RunConfigParser
{
    public RunConfigProfile Parse(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Config file not found.", path);
        }

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var argumentsNode = TryGetArgumentsNode(root);
        var keywords = ParseKeywords(root, argumentsNode);
        if (keywords.Count == 0)
        {
            throw new InvalidOperationException("Config must define at least one keyword.");
        }

        var publishedAfterDays = ParseInt(root, argumentsNode, "publishedAfterDays", 60);
        var top = ParseInt(root, argumentsNode, "top", 30);
        var useMock = ParseBool(root, argumentsNode, "useMock", false);
        var verbose = ParseBool(root, argumentsNode, "verbose", false);

        return new RunConfigProfile(keywords, publishedAfterDays, top, useMock, verbose);
    }

    private static JsonElement? TryGetArgumentsNode(JsonElement root)
    {
        if (root.TryGetProperty("arguments", out var argumentsNode) && argumentsNode.ValueKind == JsonValueKind.Object)
        {
            return argumentsNode;
        }

        return null;
    }

    private static IReadOnlyList<string> ParseKeywords(JsonElement root, JsonElement? argumentsNode)
    {
        if (TryGetProperty(root, argumentsNode, "keywords", out var keywordsElement) is false)
        {
            return [];
        }

        if (keywordsElement.ValueKind == JsonValueKind.String)
        {
            var raw = keywordsElement.GetString() ?? string.Empty;
            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToArray();
        }

        if (keywordsElement.ValueKind == JsonValueKind.Array)
        {
            return keywordsElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();
        }

        return [];
    }

    private static int ParseInt(JsonElement root, JsonElement? argumentsNode, string name, int fallback)
    {
        if (TryGetProperty(root, argumentsNode, name, out var value) is false)
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return fallback;
    }

    private static bool ParseBool(JsonElement root, JsonElement? argumentsNode, string name, bool fallback)
    {
        if (TryGetProperty(root, argumentsNode, name, out var value) is false)
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static bool TryGetProperty(JsonElement root, JsonElement? argumentsNode, string propertyName, out JsonElement value)
    {
        if (root.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (argumentsNode.HasValue && argumentsNode.Value.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
