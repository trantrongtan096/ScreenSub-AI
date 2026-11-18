using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Interfaces;

public interface ITranslationProviderResolver
{
    ITranslationProvider ResolveActiveProvider();
    ITranslationProvider GetProvider(TranslationEngineMode mode);
    string ActiveProviderBadgeText { get; }
    string StatusMessage { get; }
    TranslationEngineMode EffectiveMode { get; }
    bool IsStrictOffline { get; }
    Task<(bool Success, string Message)> TestOnlineConnectionAsync(CancellationToken ct = default)
        => Task.FromResult((false, "Không hỗ trợ kiểm tra kết nối."));
}

