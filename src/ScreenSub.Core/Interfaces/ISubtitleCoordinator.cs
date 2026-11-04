using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface ISubtitleCoordinator : IAsyncDisposable
{
    SystemState State { get; }
    long AverageLatencyMs { get; }
    int CurrentFps { get; }
    OcrResult LastOcrResult { get; }
    TranslationResult LastTranslationResult { get; }
    SubtitleItem CurrentSubtitle { get; }
    byte[]? LatestFramePreview { get; }

    event EventHandler<SystemState>? StateChanged;
    event EventHandler<SubtitleItem>? SubtitleUpdated;
    event EventHandler<byte[]>? FrameCaptured;
    event EventHandler<long>? LatencyUpdated;

    void Start();
    void Pause();
    void Stop();
    void UpdateSelectedRegion(ScreenRegion region);
    void UpdateSelectedWindow(WindowInfo? window);
}
