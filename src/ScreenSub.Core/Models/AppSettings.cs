using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Models;

public sealed class AppSettings
{
    // API Translation Settings
    public string AzureApiKey { get; set; } = string.Empty;
    public string AzureRegion { get; set; } = "eastasia";
    public string AzureEndpoint { get; set; } = "https://api.cognitive.microsofttranslator.com";
    public string GeminiApiKey { get; set; } = string.Empty;
    public string GeminiModel { get; set; } = "gemini-2.5-flash";
    public OnlineTranslationProviderType OnlineProvider { get; set; } = OnlineTranslationProviderType.Azure;
    public bool EnableContextAwareTranslation { get; set; } = true;
    public bool UseMockTranslation { get; set; } = false; // Real translation by default

    // Languages & Modes
    public SourceLanguage SourceLanguage { get; set; } = SourceLanguage.Auto;
    public List<TargetLanguage> TargetLanguages { get; set; } = [
        TargetLanguage.English,
        TargetLanguage.Chinese,
        TargetLanguage.Vietnamese
    ];
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Triple;

    // Capture & Region Settings
    public CaptureSourceType CaptureSource { get; set; } = CaptureSourceType.Window;
    public RegionMode RegionMode { get; set; } = RegionMode.FixedRegion;
    public ScreenRegion LastRegion { get; set; } = new(400, 700, 1120, 160);
    public string? LastWindowProcessName { get; set; }
    public string? LastWindowTitle { get; set; }
    public int CaptureFps { get; set; } = 4;

    // Overlay Window Settings
    public bool OverlayVisible { get; set; } = true;
    public double OverlayFontSize { get; set; } = 28.0;
    public string OverlayTextColor { get; set; } = "#FFFFFF";
    public double OverlayBackgroundOpacity { get; set; } = 0.70;
    public bool OverlayLocked { get; set; } = false;
    public bool OverlayClickThrough { get; set; } = true;
    public bool OverlayAlwaysOnTop { get; set; } = true;
    public double OverlayLeft { get; set; } = 360;
    public double OverlayTop { get; set; } = 840;
    public double OverlayWidth { get; set; } = 1200;
    public double OverlayHeight { get; set; } = 180;

    // OCR Worker Settings
    public string PythonWorkerPath { get; set; } = "workers/paddle-ocr/ocr_worker.py";
    public string PythonExePath { get; set; } = "python";
    public double FrameDiffThreshold { get; set; } = 0.03; // 3% pixel difference triggers OCR
    public int StabilityDebounceMs { get; set; } = 250;

    // Hotkey Settings
    public string HotkeyDisplayText { get; set; } = "Ctrl + Alt + Q";
    public int HotkeyModifiers { get; set; } = 3; // ModifierKeys.Control | ModifierKeys.Alt
    public int HotkeyKey { get; set; } = 60; // Key.Q

    // Audio Translation Settings (Phase 8)
    public TranslationMode TranslationMode { get; set; } = TranslationMode.ScreenOcr;
    public AudioSourceType AudioSource { get; set; } = AudioSourceType.SystemLoopback;
    public string? SelectedAudioDeviceId { get; set; }
    public string SpeechLanguage { get; set; } = "auto";
    public SpeechPerformanceMode SpeechPerformanceMode { get; set; } = SpeechPerformanceMode.Balanced;
    public string WhisperModelPath { get; set; } = "models/whisper/ggml-base.bin";
    public double VadSilenceThreshold { get; set; } = 0.015;
    public int VadMinSpeechDurationMs { get; set; } = 300;
    public int VadMaxSpeechDurationMs { get; set; } = 6000;
    public int VadHangoverMs { get; set; } = 500;

    // Offline AI & Performance Optimization (Phase 9)
    public TranslationEngineMode TranslationEngineMode { get; set; } = TranslationEngineMode.Offline;
    public AutoFallbackPolicy AutoFallbackPolicy { get; set; } = AutoFallbackPolicy.PreferOffline;
    public bool AllowCloudFallbackOnOfflineFailure { get; set; } = false; // Strict privacy: do NOT fall back silently
    public InferenceBackendType SelectedBackend { get; set; } = InferenceBackendType.Auto;
    public OverallPerformanceProfile PerformanceProfile { get; set; } = OverallPerformanceProfile.Balanced;
    public string? ModelStorageDirectory { get; set; }
    public int CpuWorkerThreads { get; set; } = 4;
    public string ActiveTranslationModelId { get; set; } = "m2m100_418m_int8";
    public bool EnableOfflineMultiModelRouter { get; set; } = false; // Default false: M2M-100 remains safe rollback until validated
    public bool EnableExperimentalZhViOpusModel { get; set; } = true;
    public bool AllowOfflineSpecializedFallbackToM2M100 { get; set; } = true;
    public int MaxLoadedTranslationModelsInMemory { get; set; } = 1;
    public bool HasExplicitlySelectedOnlineMode { get; set; } = false;
    public bool EnableDiagnosticImageLogging { get; set; } = false;

    // Quick Translate Mode & Windows Background Operation (Phase 11)
    public bool QuickTranslateMode { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool AutoPositionOverlayNearRegion { get; set; } = true;
    public bool HasCompletedFirstRunGuide { get; set; } = false;

    /// <summary>
    /// Applies the controlled EN&lt;-&gt;VI Beta Multi-Model Router profile in memory:
    /// EN&lt;-&gt;VI uses EnViT5, ZH-&gt;VI and remaining directions keep M2M-100, and MaxLoaded scales to 2 on >=8 GB RAM.
    /// Does not modify persisted production defaults unless explicitly saved.
    /// </summary>
    public void ApplyBetaEnViRouterProfile(long totalPhysicalRamBytes)
    {
        EnableOfflineMultiModelRouter = true;
        EnableExperimentalZhViOpusModel = false;
        AllowOfflineSpecializedFallbackToM2M100 = true;
        MaxLoadedTranslationModelsInMemory = totalPhysicalRamBytes >= 8L * 1024 * 1024 * 1024 ? 2 : 1;
    }

    /// <summary>
    /// Creates an isolated AppSettings instance configured for the controlled EN&lt;-&gt;VI Beta router profile.
    /// </summary>
    public static AppSettings CreateBetaEnViRouterProfile(long totalPhysicalRamBytes)
    {
        var settings = new AppSettings();
        settings.ApplyBetaEnViRouterProfile(totalPhysicalRamBytes);
        return settings;
    }

    /// <summary>
    /// Reverts offline routing back to legacy single-model M2M-100 without deleting any local model weights.
    /// </summary>
    public void ApplyLegacyM2M100RollbackProfile()
    {
        EnableOfflineMultiModelRouter = false;
        ActiveTranslationModelId = "m2m100_418m_int8";
        MaxLoadedTranslationModelsInMemory = 1;
    }
}

