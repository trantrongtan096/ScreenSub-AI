using System.Collections.Concurrent;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;
using ScreenSub.Core.Processing;

namespace ScreenSub.Translation;

public sealed class TranslationCache
{
    private readonly ConcurrentDictionary<string, (TranslationResult Result, DateTime CachedAt)> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromHours(2);
    private const int MaxCacheEntries = 10000;

    private long _hits;
    private long _requests;

    public long Hits => Interlocked.Read(ref _hits);
    public long Requests => Interlocked.Read(ref _requests);
    public double HitRate => Requests > 0 ? (double)Hits / Requests : 0.0;

    public int Count => _cache.Count;

    public bool TryGet(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        out TranslationResult? result,
        string? contextSignature = null,
        string? cacheIdentity = null)
    {
        Interlocked.Increment(ref _requests);
        string key = BuildKey(text, source, targets, contextSignature, cacheIdentity);
        if (_cache.TryGetValue(key, out var entry))
        {
            if (DateTime.UtcNow - entry.CachedAt < _ttl)
            {
                Interlocked.Increment(ref _hits);
                result = new TranslationResult
                {
                    OriginalText = entry.Result.OriginalText,
                    DetectedLanguage = entry.Result.DetectedLanguage,
                    Translations = new Dictionary<TargetLanguage, string>(entry.Result.Translations),
                    LatencyMs = 0,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = true,
                    ProviderName = entry.Result.ProviderName,
                    ModelUsed = entry.Result.ModelUsed,
                    ModelFingerprint = entry.Result.ModelFingerprint,
                    UsedFallbackModel = entry.Result.UsedFallbackModel
                };
                return true;
            }

            _cache.TryRemove(key, out _);
        }

        result = null;
        return false;
    }

    public void Set(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        TranslationResult result,
        string? contextSignature = null,
        string? cacheIdentity = null)
    {
        if (result == null || result.Translations == null || result.Translations.Count == 0) return;

        // Never cache error markers, lexical fallbacks, or invalid placeholders
        if (result.Translations.Values.Any(v =>
            string.IsNullOrWhiteSpace(v) ||
            v.StartsWith("[MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("[LEXICAL", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("[Translation", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("[Azure", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("[Gemini", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("[Lỗi", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("[Chưa", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (_cache.Count > MaxCacheEntries)
        {
            // Evict 20% oldest entries
            var oldestKeys = _cache.OrderBy(kv => kv.Value.CachedAt).Take(2000).Select(kv => kv.Key).ToList();
            foreach (var k in oldestKeys)
            {
                _cache.TryRemove(k, out _);
            }
        }

        string key = BuildKey(text, source, targets, contextSignature, cacheIdentity);
        _cache[key] = (result, DateTime.UtcNow);
    }

    public void InvalidateCorrupted()
    {
        var corruptedKeys = _cache.Keys.Where(k =>
            k.Contains("[MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase) ||
            k.Contains("[Azure", StringComparison.OrdinalIgnoreCase) ||
            k.Contains("[Gemini", StringComparison.OrdinalIgnoreCase) ||
            k.Contains("[Lỗi", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var k in corruptedKeys)
        {
            _cache.TryRemove(k, out _);
        }
    }

    public void Clear()
    {
        _cache.Clear();
    }

    private static string BuildKey(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        string? contextSignature = null,
        string? cacheIdentity = null)
    {
        var targetStr = string.Join(",", targets.OrderBy(t => t));
        string clean = SubtitleTextProcessor.RestoreWordSpacing(text.Trim());
        string idPart = string.IsNullOrEmpty(cacheIdentity) ? "default" : cacheIdentity;
        return string.IsNullOrEmpty(contextSignature)
            ? $"id={idPart}|{source}:{targetStr}:{clean}"
            : $"id={idPart}|{source}:{targetStr}:ctx={contextSignature}:{clean}";
    }
}

