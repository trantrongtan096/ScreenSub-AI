using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Translation;

public sealed class TranslationProviderResolver : ITranslationProviderResolver, IContextAwareTranslationProvider
{
    private readonly ISettingsService _settingsService;
    private readonly LocalTranslationProvider _localProvider;
    private readonly AzureTranslatorProvider _azureProvider;
    private readonly MockTranslationProvider _mockProvider;
    private readonly GeminiTranslationProvider? _geminiProvider;

    public TranslationProviderResolver(
        ISettingsService settingsService,
        LocalTranslationProvider localProvider,
        AzureTranslatorProvider azureProvider,
        MockTranslationProvider mockProvider,
        GeminiTranslationProvider? geminiProvider = null)
    {
        _settingsService = settingsService;
        _localProvider = localProvider;
        _azureProvider = azureProvider;
        _mockProvider = mockProvider;
        _geminiProvider = geminiProvider;
    }

    public string ProviderName => ResolveActiveProvider().ProviderName;
    public bool IsMock => ResolveActiveProvider().IsMock;
    public bool IsOffline => ResolveActiveProvider().IsOffline;
    public ServiceStatus Status => ResolveActiveProvider().Status;
    public string StatusMessage => ResolveActiveProvider().StatusMessage;

    public bool IsStrictOffline
    {
        get
        {
            var mode = _settingsService.Settings.TranslationEngineMode;
            if (mode == TranslationEngineMode.Offline) return true;
            if (mode == TranslationEngineMode.Auto && !_settingsService.Settings.AllowCloudFallbackOnOfflineFailure && _settingsService.Settings.AutoFallbackPolicy == AutoFallbackPolicy.PreferOffline)
                return true;
            return false;
        }
    }

    public TranslationEngineMode EffectiveMode
    {
        get
        {
            var provider = ResolveActiveProvider();
            if (provider == _localProvider) return TranslationEngineMode.Offline;
            if (provider == _azureProvider || provider == _geminiProvider) return TranslationEngineMode.Online;
            return _settingsService.Settings.TranslationEngineMode;
        }
    }

    public string ActiveProviderBadgeText
    {
        get
        {
            var provider = ResolveActiveProvider();
            if (provider.IsMock) return "DEMO — MOCK";
            if (provider.IsOffline)
            {
                if (provider == _localProvider && !_localProvider.IsModelInstalled)
                {
                    return "OFFLINE AI — CHƯA CÀI MÔ HÌNH";
                }
                return "OFFLINE AI — LOCAL PROCESSING";
            }
            if (provider == _geminiProvider)
            {
                return HasGeminiConfigured() ? "ONLINE — GEMINI CONTEXTUAL AI" : "GEMINI — CHƯA CẤU HÌNH";
            }
            if (!HasAzureConfigured())
            {
                return "AZURE — CHƯA CẤU HÌNH";
            }
            return "ONLINE — AZURE TRANSLATOR";
        }
    }

    public ITranslationProvider GetProvider(TranslationEngineMode mode)
    {
        return mode switch
        {
            TranslationEngineMode.Offline => _localProvider,
            TranslationEngineMode.Online => ResolvePreferredOnlineProvider() ?? (_settingsService.Settings.UseMockTranslation ? _mockProvider : GetDefaultOnlineProvider()),
            _ => ResolveActiveProvider()
        };
    }

    public ITranslationProvider ResolveActiveProvider()
    {
        var settings = _settingsService.Settings;
        switch (settings.TranslationEngineMode)
        {
            case TranslationEngineMode.Offline:
                return _localProvider;

            case TranslationEngineMode.Online:
                var online = ResolvePreferredOnlineProvider();
                if (online != null)
                    return online;
                return settings.UseMockTranslation ? _mockProvider : GetDefaultOnlineProvider();

            case TranslationEngineMode.Auto:
            default:
                switch (settings.AutoFallbackPolicy)
                {
                    case AutoFallbackPolicy.PreferOnline:
                        var prefOnline = ResolvePreferredOnlineProvider();
                        if (prefOnline != null)
                            return prefOnline;
                        return _localProvider;

                    case AutoFallbackPolicy.PreferOffline:
                    case AutoFallbackPolicy.ManualOnly:
                    default:
                        return _localProvider;
                }
        }
    }

    public Task<TranslationResult> TranslateAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct = default)
    {
        return TranslateWithContextAsync(text, source, targets, Array.Empty<SubtitleItem>(), ct);
    }

    public async Task<TranslationResult> TranslateWithContextAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        IReadOnlyList<SubtitleItem> recentContext,
        CancellationToken ct = default)
    {
        var settings = _settingsService.Settings;
        var active = ResolveActiveProvider();

        try
        {
            return await ExecuteProviderTranslationAsync(active, text, source, targets, recentContext, settings.EnableContextAwareTranslation, ct);
        }
        catch (Exception) when (settings.TranslationEngineMode == TranslationEngineMode.Auto)
        {
            // Auto fallback handling:
            if (active == _localProvider && settings.AllowCloudFallbackOnOfflineFailure)
            {
                var cloudFallback = ResolvePreferredOnlineProvider();
                if (cloudFallback != null)
                {
                    return await ExecuteProviderTranslationAsync(cloudFallback, text, source, targets, recentContext, settings.EnableContextAwareTranslation, ct);
                }
            }
            else if (active == _azureProvider || active == _geminiProvider)
            {
                // Fallback to Local provider on network/cloud failure
                return await _localProvider.TranslateAsync(text, source, targets, ct);
            }

            throw;
        }
    }

    private static Task<TranslationResult> ExecuteProviderTranslationAsync(
        ITranslationProvider provider,
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        IReadOnlyList<SubtitleItem> recentContext,
        bool enableContext,
        CancellationToken ct)
    {
        if (enableContext && recentContext.Count > 0 && provider is IContextAwareTranslationProvider contextProvider)
        {
            return contextProvider.TranslateWithContextAsync(text, source, targets, recentContext, ct);
        }

        return provider.TranslateAsync(text, source, targets, ct);
    }

    private ITranslationProvider GetDefaultOnlineProvider()
    {
        if (_settingsService.Settings.OnlineProvider == OnlineTranslationProviderType.Gemini && _geminiProvider != null)
        {
            return _geminiProvider;
        }
        return _azureProvider;
    }

    private ITranslationProvider? ResolvePreferredOnlineProvider()
    {
        var settings = _settingsService.Settings;
        if (settings.OnlineProvider == OnlineTranslationProviderType.Gemini)
        {
            if (_geminiProvider != null && HasGeminiConfigured())
                return _geminiProvider;
            if (HasAzureConfigured())
                return _azureProvider;
            return null;
        }

        if (HasAzureConfigured())
            return _azureProvider;
        if (_geminiProvider != null && HasGeminiConfigured())
            return _geminiProvider;
        return null;
    }

    private bool HasGeminiConfigured()
    {
        if (_geminiProvider == null) return false;
        var apiKey = _settingsService.GetSecret("GeminiApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _settingsService.Settings.GeminiApiKey;
        }
        return !string.IsNullOrWhiteSpace(apiKey);
    }

    private bool HasAzureConfigured()
    {
        var s = _settingsService.Settings;
        var apiKey = _settingsService.GetSecret("AzureApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = s.AzureApiKey;
        }

        return !string.IsNullOrWhiteSpace(apiKey);
    }

    public async Task<(bool Success, string Message)> TestOnlineConnectionAsync(CancellationToken ct = default)
    {
        if (_settingsService.Settings.OnlineProvider == OnlineTranslationProviderType.Gemini && _geminiProvider != null)
        {
            return await _geminiProvider.TestConnectionAsync(ct);
        }

        return await _azureProvider.TestConnectionAsync(ct);
    }
}
