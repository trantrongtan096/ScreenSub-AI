using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Models;

public sealed class SubtitleItem
{
    public static readonly SubtitleItem Empty = new();

    public string OriginalText { get; set; } = string.Empty;
    public string OriginalLanguageCode { get; set; } = "zh";
    public string EnglishText { get; set; } = string.Empty;
    public string ChineseText { get; set; } = string.Empty;
    public string VietnameseText { get; set; } = string.Empty;
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Triple;
    public TranslationMode SourceMode { get; set; } = TranslationMode.ScreenOcr;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Request identity metadata for end-to-end stale-result UI protection
    public string SessionId { get; set; } = string.Empty;
    public long SequenceId { get; set; }
    public long SettingsVersion { get; set; }

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(OriginalText) ||
        !string.IsNullOrWhiteSpace(EnglishText) ||
        !string.IsNullOrWhiteSpace(VietnameseText) ||
        !string.IsNullOrWhiteSpace(ChineseText);
}

