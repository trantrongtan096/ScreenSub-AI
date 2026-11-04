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
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(OriginalText) ||
        !string.IsNullOrWhiteSpace(EnglishText) ||
        !string.IsNullOrWhiteSpace(VietnameseText) ||
        !string.IsNullOrWhiteSpace(ChineseText);
}
