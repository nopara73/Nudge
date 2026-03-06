namespace Nudge.Cli.Services;

public sealed class PodchaserTokenMemory
{
    private readonly string _filePath;

    public PodchaserTokenMemory()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nudge.Cli",
                "podchaser-last-good-token.txt"))
    {
    }

    public PodchaserTokenMemory(string filePath)
    {
        _filePath = filePath;
    }

    public string? LoadLastKnownGoodToken()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            var token = File.ReadAllText(_filePath).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    public void RememberToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        try
        {
            var directoryPath = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            File.WriteAllText(_filePath, token.Trim());
        }
        catch
        {
            // Token caching should never block normal execution.
        }
    }

    public static IReadOnlyList<string> PrioritizeRememberedToken(IReadOnlyList<string> candidateTokens, string? rememberedToken)
    {
        if (candidateTokens.Count == 0 || string.IsNullOrWhiteSpace(rememberedToken))
        {
            return candidateTokens;
        }

        var rememberedIndex = -1;
        for (var i = 0; i < candidateTokens.Count; i++)
        {
            if (string.Equals(candidateTokens[i], rememberedToken, StringComparison.Ordinal))
            {
                rememberedIndex = i;
                break;
            }
        }

        if (rememberedIndex <= 0)
        {
            return candidateTokens;
        }

        var reorderedTokens = new List<string>(candidateTokens.Count)
        {
            candidateTokens[rememberedIndex]
        };

        for (var i = 0; i < candidateTokens.Count; i++)
        {
            if (i != rememberedIndex)
            {
                reorderedTokens.Add(candidateTokens[i]);
            }
        }

        return reorderedTokens;
    }
}
