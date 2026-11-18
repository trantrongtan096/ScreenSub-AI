namespace ScreenSub.Core.Enums;

public enum CaptureSourceType
{
    FullScreen,
    Window
}

public enum RegionMode
{
    FixedRegion,
    FollowWindow
}

public enum DisplayMode
{
    Single,
    Dual,
    Triple
}

public enum SourceLanguage
{
    Auto,
    English,
    Chinese,
    Vietnamese
}

public enum TargetLanguage
{
    English,
    Chinese,
    Vietnamese
}

public enum SystemState
{
    Idle,
    SelectingRegion,
    Starting,
    Translating,
    Running = Translating,
    Paused,
    Stopped,
    Error
}

public enum ServiceStatus
{
    Stopped,
    Starting,
    Running,
    Connected,
    Degraded,
    Error
}

public enum ModelRuntimeState
{
    NotInstalled,
    Loading,
    Ready,
    InferenceRunning,
    Error
}

public enum TranslationMode
{
    ScreenOcr,
    AudioRealtime,
    Hybrid
}

public enum VadState
{
    Silence,
    SpeechDetected,
    SpeechInProgress,
    SpeechEnded
}

public enum SpeechPerformanceMode
{
    Fast,     // Tiny model - lower latency
    Balanced, // Base model - default balance
    Quality   // Small model - higher accuracy
}

public enum AudioSourceType
{
    SystemLoopback,
    SpecificDevice
}

public enum TranslationEngineMode
{
    Online,  // Azure AI Translator
    Offline, // Local AI Offline Inference
    Auto     // Fallback according to user policy
}

public enum AutoFallbackPolicy
{
    PreferOffline, // Use offline local AI, fallback to online if allowed
    PreferOnline,  // Use online Azure AI, fallback to offline if network fails
    ManualOnly     // Strict mode: no auto fallback
}

public enum InferenceBackendType
{
    Auto,
    Cpu,
    DirectMl,
    Cuda,
    Vulkan
}

public enum ModelCategory
{
    Ocr,
    Speech,
    Translation
}

public enum ModelInstallationStatus
{
    NotInstalled,
    Downloading,
    Installing,
    Installed,
    Active,
    Error
}

public enum OverallPerformanceProfile
{
    Fast,
    Balanced,
    Quality,
    Custom
}

public enum OnlineTranslationProviderType
{
    Azure,
    Gemini
}


