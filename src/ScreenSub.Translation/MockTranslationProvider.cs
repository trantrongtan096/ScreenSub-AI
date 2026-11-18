using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Translation;

public sealed class MockTranslationProvider : ITranslationProvider
{
    public string ProviderName => "Mock Translation (Chế độ Thử nghiệm)";
    public bool IsMock => true;
    public ServiceStatus Status => ServiceStatus.Connected;
    public string StatusMessage => "Chế độ Mock (Demo)";

    private static readonly Dictionary<string, (string En, string Vi, string Zh)> Dictionary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["你今天有时间吗？"] = ("Are you free today?", "Hôm nay bạn có rảnh không?", "你今天有时间吗？"),
        ["你今天有时间吗?"] = ("Are you free today?", "Hôm nay bạn có rảnh không?", "你今天有时间吗?"),
        ["你好"] = ("Hello", "Xin chào", "你好"),
        ["谢谢"] = ("Thank you", "Cảm ơn bạn", "谢谢"),
        ["再见"] = ("Goodbye", "Tạm biệt", "再见"),
        ["我爱你"] = ("I love you", "Tôi yêu bạn", "我爱你"),
        ["对不起"] = ("Sorry", "Xin lỗi", "对不起"),
        ["没关系"] = ("It doesn't matter", "Không sao đâu", "没关系"),
        ["Hello"] = ("Hello", "Xin chào", "你好"),
        ["Are you free today?"] = ("Are you free today?", "Hôm nay bạn có rảnh không?", "你今天有时间吗？"),
        ["Hôm nay bạn có rảnh không?"] = ("Are you free today?", "Hôm nay bạn có rảnh không?", "你今天有时间吗？")
    };

    public Task<TranslationResult> TranslateAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(TranslationResult.Empty);
        }

        var translations = new Dictionary<TargetLanguage, string>();
        var trimmed = text.Trim();

        string detected = source == SourceLanguage.Auto ? DetectSimpleLanguage(trimmed) : source.ToString();

        if (Dictionary.TryGetValue(trimmed, out var match))
        {
            foreach (var t in targets)
            {
                translations[t] = t switch
                {
                    TargetLanguage.English => match.En,
                    TargetLanguage.Vietnamese => match.Vi,
                    TargetLanguage.Chinese => match.Zh,
                    _ => match.Vi
                };
            }
        }
        else
        {
            // Generic mock translation for other phrases
            foreach (var t in targets)
            {
                translations[t] = t switch
                {
                    TargetLanguage.English => $"[EN] {trimmed}",
                    TargetLanguage.Vietnamese => $"[VI] {trimmed}",
                    TargetLanguage.Chinese => $"[ZH] {trimmed}",
                    _ => trimmed
                };
            }
        }

        var result = new TranslationResult
        {
            OriginalText = trimmed,
            DetectedLanguage = detected,
            Translations = translations,
            LatencyMs = 45,
            Timestamp = DateTime.UtcNow,
            IsFromCache = false,
            ProviderName = ProviderName
        };

        return Task.FromResult(result);
    }

    private static string DetectSimpleLanguage(string text)
    {
        // Simple CJK detection
        if (text.Any(c => c >= 0x4E00 && c <= 0x9FFF))
        {
            return "zh-Hans";
        }

        // Vietnamese specific diacritics
        string viChars = "àáảãạăằắẳẵặâầấẩẫậèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵđ";
        if (text.Any(c => viChars.Contains(char.ToLower(c))))
        {
            return "vi";
        }

        return "en";
    }
}

