using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Models;

public sealed class TranslationResult
{
    public static readonly TranslationResult Empty = new()
    {
        OriginalText = string.Empty,
        DetectedLanguage = string.Empty,
        Translations = [],
        LatencyMs = 0,
        Timestamp = DateTime.UtcNow,
        IsFromCache = false
    };

    public string OriginalText { get; set; } = string.Empty;
    public string DetectedLanguage { get; set; } = string.Empty;
    public Dictionary<TargetLanguage, string> Translations { get; set; } = [];
    public long LatencyMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool IsFromCache { get; set; }
    public string ProviderName { get; set; } = string.Empty;

    public string GetTranslation(TargetLanguage lang)
    {
        return Translations.TryGetValue(lang, out var val) ? val : string.Empty;
    }
}
