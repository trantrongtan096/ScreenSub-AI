using System.Diagnostics;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Core.Processing;

namespace ScreenSub.Translation;

/// <summary>
/// Offline AI Translation Provider.
/// Executes 100% locally on Windows using M2M-100 418M INT8 (CTranslate2).
/// Does NOT perform any network requests or transmit subtitles over the internet.
/// </summary>
public sealed class LocalTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly LocalNeuralTranslationEngine _engine;
    private readonly TranslationCache _cache;
    private readonly ISettingsService _settingsService;

    public string ProviderName => "Local AI (Offline)";
    public bool IsMock => false;
    public bool IsOffline => true;
    public bool IsModelInstalled => _engine.State == ModelRuntimeState.Ready ||
                                    _engine.State == ModelRuntimeState.InferenceRunning ||
                                    _engine.State == ModelRuntimeState.Loading ||
                                    LocalNeuralTranslationEngine.IsAnyTranslationModelInstalledOnDisk(_engine.CustomModelsRoot);

    public ModelRuntimeState ModelState => _engine.State;
    public LocalNeuralTranslationEngine Engine => _engine;
    public string ActiveModelName => !string.IsNullOrEmpty(_engine.ActiveModelName) ? _engine.ActiveModelName : "M2M-100 418M (INT8)";

    public ServiceStatus Status
    {
        get
        {
            if (_engine.State == ModelRuntimeState.Ready || _engine.State == ModelRuntimeState.InferenceRunning)
            {
                return ServiceStatus.Connected;
            }
            if (_engine.State == ModelRuntimeState.Loading)
            {
                return ServiceStatus.Starting;
            }
            if (_engine.AllowLexicalFallback)
            {
                return ServiceStatus.Degraded;
            }
            return ServiceStatus.Error;
        }
    }

    private string? _runtimeStatusMessage;
    public string StatusMessage
    {
        get
        {
            if (_engine.State == ModelRuntimeState.NotInstalled && !_engine.AllowLexicalFallback)
            {
                return "Chưa cài đặt mô hình dịch Offline";
            }
            if (_engine.State == ModelRuntimeState.Loading)
            {
                return $"Đang nạp {ActiveModelName}...";
            }
            if (_engine.State == ModelRuntimeState.InferenceRunning)
            {
                return $"Đang dịch ({ActiveModelName})...";
            }
            return _runtimeStatusMessage ?? _engine.StatusMessage;
        }
        private set => _runtimeStatusMessage = value;
    }

    public LocalTranslationProvider(
        LocalNeuralTranslationEngine engine,
        TranslationCache cache,
        ISettingsService settingsService)
    {
        _engine = engine;
        _cache = cache;
        _settingsService = settingsService;
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Empty;
        }

        string cleanText = SubtitleTextProcessor.RestoreWordSpacing(text.Trim());
        string srcCode = MapSourceLanguage(source);

        // Detect source language BEFORE cache lookup so router cache keys reflect actual source->target model identity
        string detectedLang = srcCode;
        string realTextLang = SubtitleTextProcessor.DetectLanguage(cleanText);
        if (srcCode is "auto" or "" || realTextLang != "auto")
        {
            if (realTextLang != "auto")
            {
                detectedLang = realTextLang;
            }
            else
            {
                bool hasCjk = cleanText.Any(SubtitleTextProcessor.IsCjkCharacter);
                bool hasLatin = cleanText.Any(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
                detectedLang = hasCjk ? "zh" : (hasLatin ? "en" : "en");
            }
        }

        string lookupIdentity = _engine.BuildCacheIdentityForTargets(detectedLang, targets);

        // 1. Check Cache (scoped by routed model_used + model_fingerprint + decoding_config)
        if (_cache.TryGet(cleanText, source, targets, out var cachedResult, contextSignature: null, cacheIdentity: lookupIdentity) && cachedResult != null)
        {
            StatusMessage = "Offline AI (Cache Hit)";
            return cachedResult;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            var translations = new Dictionary<TargetLanguage, string>();
            var detailedByTarget = new Dictionary<TargetLanguage, NeuralTranslationResponse>();
            var usedModels = new List<string>();
            var usedFingerprints = new List<string>();
            bool anyFallback = false;

            foreach (var target in targets)
            {
                string tgtCode = MapTargetLanguage(target);
                string translated;

                if (string.Equals(detectedLang, tgtCode, StringComparison.OrdinalIgnoreCase))
                {
                    translated = cleanText;
                }
                else
                {
                    var neuralResp = await _engine.TranslateTextDetailedAsync(cleanText, detectedLang, tgtCode, ct);
                    if (neuralResp.TranslatedText.StartsWith("[MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase))
                    {
                        StatusMessage = "MODEL NOT INSTALLED";
                        throw new InvalidOperationException("MODEL NOT INSTALLED: Chưa cài đặt mô hình dịch Offline trong tab Cài đặt.");
                    }

                    detailedByTarget[target] = neuralResp;
                    if (!string.IsNullOrWhiteSpace(neuralResp.ModelUsed) && !usedModels.Contains(neuralResp.ModelUsed, StringComparer.OrdinalIgnoreCase))
                    {
                        usedModels.Add(neuralResp.ModelUsed);
                    }
                    if (!string.IsNullOrWhiteSpace(neuralResp.ModelFingerprint) && !usedFingerprints.Contains(neuralResp.ModelFingerprint, StringComparer.OrdinalIgnoreCase))
                    {
                        usedFingerprints.Add(neuralResp.ModelFingerprint);
                    }
                    if (neuralResp.FallbackUsed)
                    {
                        anyFallback = true;
                    }

                    translated = SubtitleTextProcessor.CleanTranslationOutput(neuralResp.TranslatedText, cleanText);
                    translated = SubtitleTextProcessor.RestoreWordSpacing(translated);
                }

                translations[target] = translated;
            }

            sw.Stop();

            string summaryModelUsed = usedModels.Count > 0 ? string.Join(",", usedModels) : _engine.LastModelUsed;
            string summaryFingerprint = usedFingerprints.Count > 0 ? string.Join(",", usedFingerprints) : _engine.ModelFingerprint;
            string displayLabel = !string.IsNullOrEmpty(_engine.ActiveModelName) ? _engine.ActiveModelName : summaryModelUsed;

            var result = new TranslationResult
            {
                OriginalText = cleanText,
                DetectedLanguage = detectedLang,
                Translations = translations,
                LatencyMs = sw.ElapsedMilliseconds,
                Timestamp = DateTime.UtcNow,
                ProviderName = $"Local AI ({displayLabel})",
                ModelUsed = summaryModelUsed,
                ModelFingerprint = summaryFingerprint,
                UsedFallbackModel = anyFallback
            };

            // Store using both actual response identity (and lookup identity if fingerprints weren't yet known before first inference)
            string storeIdentity = _engine.BuildCacheIdentityForTargets(detectedLang, targets, detailedByTarget);
            _cache.Set(cleanText, source, targets, result, contextSignature: null, cacheIdentity: storeIdentity);
            if (!string.Equals(storeIdentity, lookupIdentity, StringComparison.Ordinal))
            {
                // Subsequent lookups will resolve lookupIdentity using updated _knownFingerprints from _engine;
                // storeIdentity == lookupIdentity after first load!
            }

            StatusMessage = anyFallback
                ? $"Offline AI ({displayLabel} [Fallback], {sw.ElapsedMilliseconds} ms)"
                : $"Offline AI ({displayLabel}, {sw.ElapsedMilliseconds} ms)";
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi Offline AI: {ex.Message}";
            throw;
        }
    }

    private static string MapSourceLanguage(SourceLanguage lang) => lang switch
    {
        SourceLanguage.English => "en",
        SourceLanguage.Chinese => "zh",
        SourceLanguage.Vietnamese => "vi",
        _ => "auto"
    };

    private static string MapTargetLanguage(TargetLanguage lang) => lang switch
    {
        TargetLanguage.English => "en",
        TargetLanguage.Chinese => "zh",
        TargetLanguage.Vietnamese => "vi",
        _ => "vi"
    };

    public void Dispose()
    {
        _engine.Dispose();
    }
}
