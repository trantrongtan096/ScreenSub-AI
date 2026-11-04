using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface ITranslationProvider
{
    string ProviderName { get; }
    bool IsMock { get; }
    ServiceStatus Status { get; }
    Task<TranslationResult> TranslateAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct = default);
}
