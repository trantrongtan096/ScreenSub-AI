using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface ISubtitleCoordinator : IAsyncDisposable
{
    SystemState State { get; }
    long AverageLatencyMs { get; }
    int CurrentFps { get; }
    long LastCaptureLatencyMs { get; }
    long LastOcrLatencyMs { get; }
    long LastTranslationLatencyMs { get; }
    long LastOverlayUpdateLatencyMs { get; set; }
    long LastEndToEndLatencyMs { get; }
    int OcrQueueLength { get; }
    double CacheHitRate { get; }
    long CaptureFrameCount { get; }
    long OcrAttemptCount { get; }
    long OcrSuccessCount { get; }
    CaptureFrameResult LastCaptureDiagnostics { get; }
    OcrResult LastOcrResult { get; }
    string LastOcrRawText { get; }
    string LastNormalizedText { get; }
    TranslationResult LastTranslationResult { get; }
    SubtitleItem CurrentSubtitle { get; }
    byte[]? LatestFramePreview { get; }
    string OcrStatusText { get; }
    string TranslationStatusText { get; }
    string PipelineStatusText { get; }
    bool IsMockMode { get; }
    bool HasActiveCapture { get; }
    string ActiveProviderBadgeText { get; }

    // Mode & Audio Properties (Phase 8)
    TranslationMode Mode { get; }
    void SetTranslationMode(TranslationMode mode);
    IReadOnlyList<AudioDeviceInfo> OutputAudioDevices { get; }
    AudioDeviceInfo? SelectedAudioDevice { get; }
    void SetSelectedAudioDevice(AudioDeviceInfo? device);
    void RefreshAudioDevices();
    float AudioPeakLevel { get; }
    float AudioRmsLevel { get; }
    string SpeechStatusText { get; }
    string LastRecognizedSpeechText { get; }
    string LastSpeechLanguage { get; }
    long LastSpeechLatencyMs { get; }
    string CurrentSessionId { get; }
    long SettingsVersion { get; }
    long DroppedObsoleteCount { get; }
    long RejectedStaleCount { get; }
    long LastQueueWaitMs { get; }

    event EventHandler<SystemState>? StateChanged;
    event EventHandler<(string Text, string LanguageCode)>? OcrTextRecognized;
    event EventHandler<SubtitleItem>? SubtitleUpdated;
    event EventHandler<byte[]>? FrameCaptured;
    event EventHandler<long>? LatencyUpdated;
    event EventHandler<string>? StatusUpdated;
    event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    event EventHandler<SpeechRecognitionResult>? SpeechRecognized;

    void Start();
    void Pause();
    void Stop();
    void UpdateSelectedRegion(ScreenRegion region);
    void UpdateSelectedWindow(WindowInfo? window);

    // Quick Translate & Lifecycle State Machine
    void NotifySelectingRegion();
    void CancelSelectingRegion();
    void UpdateSelectedRegionAndAutoStart(ScreenRegion region);
    void ResetTranslationCacheAndRetranslate();
    Task<(bool Success, string Message)> TestOnlineConnectionAsync(CancellationToken ct = default);
}
