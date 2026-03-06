using System.Text.Json;
using Nudge.Core.Models;

namespace Nudge.Cli.Services;

public sealed class PodchaserSearchCache
{
    private const int MaxEntries = 64;
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(6);
    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;
    private readonly object _gate = new();

    public PodchaserSearchCache()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Nudge.Cli",
                "podchaser-search-cache.json"),
            TimeProvider.System,
            DefaultTtl)
    {
    }

    public PodchaserSearchCache(string filePath, TimeProvider timeProvider, TimeSpan ttl)
    {
        _filePath = filePath;
        _timeProvider = timeProvider;
        _ttl = ttl <= TimeSpan.Zero ? DefaultTtl : ttl;
    }

    public bool TryGet(string cacheKey, out IReadOnlyList<PodcastSearchResult> results)
    {
        lock (_gate)
        {
            var entries = LoadEntries();
            var wasPruned = PruneExpiredEntries(entries);
            if (!entries.TryGetValue(cacheKey, out var entry))
            {
                if (wasPruned)
                {
                    SaveEntries(entries);
                }

                results = Array.Empty<PodcastSearchResult>();
                return false;
            }

            results = entry.Results ?? Array.Empty<PodcastSearchResult>();
            if (wasPruned)
            {
                SaveEntries(entries);
            }

            return true;
        }
    }

    public void Set(string cacheKey, IReadOnlyList<PodcastSearchResult> results)
    {
        lock (_gate)
        {
            var entries = LoadEntries();
            if (results.Count == 0)
            {
                if (entries.Remove(cacheKey))
                {
                    SaveEntries(entries);
                }

                return;
            }

            entries[cacheKey] = new CacheEntry
            {
                StoredAtUtc = _timeProvider.GetUtcNow(),
                Results = results.ToArray()
            };

            PruneExpiredEntries(entries);
            TrimToMaxEntries(entries);
            SaveEntries(entries);
        }
    }

    private Dictionary<string, CacheEntry> LoadEntries()
    {
        if (!File.Exists(_filePath))
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json);
            return parsed is null
                ? new Dictionary<string, CacheEntry>(StringComparer.Ordinal)
                : new Dictionary<string, CacheEntry>(parsed, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        }
    }

    private void SaveEntries(Dictionary<string, CacheEntry> entries)
    {
        try
        {
            var directoryPath = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Search caching should never block CLI execution.
        }
    }

    private bool PruneExpiredEntries(Dictionary<string, CacheEntry> entries)
    {
        var now = _timeProvider.GetUtcNow();
        var expiredKeys = new List<string>();
        foreach (var pair in entries)
        {
            if (pair.Value is null || now - pair.Value.StoredAtUtc > _ttl)
            {
                expiredKeys.Add(pair.Key);
            }
        }

        if (expiredKeys.Count == 0)
        {
            return false;
        }

        foreach (var key in expiredKeys.Distinct(StringComparer.Ordinal))
        {
            entries.Remove(key);
        }

        return true;
    }

    private static void TrimToMaxEntries(Dictionary<string, CacheEntry> entries)
    {
        if (entries.Count <= MaxEntries)
        {
            return;
        }

        var keysToRemove = entries
            .OrderByDescending(static pair => pair.Value.StoredAtUtc)
            .Skip(MaxEntries)
            .Select(static pair => pair.Key)
            .ToArray();

        foreach (var key in keysToRemove)
        {
            entries.Remove(key);
        }
    }

    private sealed record CacheEntry
    {
        public DateTimeOffset StoredAtUtc { get; init; }
        public PodcastSearchResult[] Results { get; init; } = Array.Empty<PodcastSearchResult>();
    }
}
