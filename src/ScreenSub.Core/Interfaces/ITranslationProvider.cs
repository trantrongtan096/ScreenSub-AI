using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface ITranslationProvider
{
    string ProviderName { get; }
    bool IsMock { get; }
    bool IsOffline => false;
    ServiceStatus Status { get; }
    string StatusMessage { get; }
    Task<TranslationResult> TranslateAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct = default);
}

public interface IContextAwareTranslationProvider : ITranslationProvider
{
    Task<TranslationResult> TranslateWithContextAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        IReadOnlyList<SubtitleItem> recentContext,
        CancellationToken ct = default);
}
