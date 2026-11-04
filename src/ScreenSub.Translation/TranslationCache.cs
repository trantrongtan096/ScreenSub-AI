using System.Collections.Concurrent;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Translation;

public sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, (TranslationResult Result, DateTime CachedAt)> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromHours(2);
    private const int MaxCacheEntries = 10000;

    public bool TryGet(string text, SourceLanguage source, IReadOnlyList<TargetLanguage> targets, out TranslationResult? result)
    {
        string key = BuildKey(text, source, targets);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow - entry.CachedAt < _ttl)
            {
                result = new TranslationResult
                {
                    OriginalText = entry.Result.OriginalText,
                    DetectedLanguage = entry.Result.DetectedLanguage,
                    Translations = new Dictionary<TargetLanguage, string>(entry.Result.Translations),
                    LatencyMs = 0,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = true,
                    ProviderName = entry.Result.ProviderName
                };
                return true;
            }

            _cache.TryRemove(key, out _);
        }

        result = null;
        return false;
    }

    public void Set(string text, SourceLanguage source, IReadOnlyList<TargetLanguage> targets, TranslationResult result)
    {
        if (_cache.Count > MaxCacheEntries)
        {
            // Evict 20% oldest entries
            var oldestKeys = _cache.OrderBy(kv => kv.Value.CachedAt).Take(2000).Select(kv => kv.Key).ToList();
            foreach (var k in oldestKeys)
            {
                _cache.TryRemove(k, out _);
            }
        }

        string key = BuildKey(text, source, targets);
        _cache[key] = (result, DateTime.UtcNow);
    }

    public void Clear()
    {
        _cache.Clear();
    }

    private static string BuildKey(string text, SourceLanguage source, IReadOnlyList<TargetLanguage> targets)
    {
        var targetStr = string.Join(",", targets.OrderBy(t => t));
        return $"{source}:{targetStr}:{text.Trim()}";
    }
}
