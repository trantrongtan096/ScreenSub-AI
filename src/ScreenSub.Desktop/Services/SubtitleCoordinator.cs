using System.Diagnostics;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Desktop.Services;

public sealed class SubtitleCoordinator : ISubtitleCoordinator
{
    private readonly IScreenCaptureService _captureService;
    private readonly IOcrEngine _ocrEngine;
    private readonly ITranslationProvider _azureProvider;
    private readonly ITranslationProvider _mockProvider;
    private readonly ISettingsService _settingsService;
    private readonly IFrameChangeDetector _changeDetector;
    private readonly WindowRegionTracker _windowTracker;

    private SystemState _state = SystemState.Idle;
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private ScreenRegion _selectedRegion;
    private WindowInfo? _selectedWindow;

    private readonly Queue<long> _latencyHistory = new();
    private const int MaxLatencySamples = 10;

    public SystemState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, _state);
            }
        }
    }

    public long AverageLatencyMs { get; private set; } = 420;
    public int CurrentFps { get; private set; } = 0;
    public OcrResult LastOcrResult { get; private set; } = OcrResult.Empty;
    public TranslationResult LastTranslationResult { get; private set; } = TranslationResult.Empty;
    public SubtitleItem CurrentSubtitle { get; private set; } = SubtitleItem.Empty;
    public byte[]? LatestFramePreview { get; private set; }

    public event EventHandler<SystemState>? StateChanged;
    public event EventHandler<SubtitleItem>? SubtitleUpdated;
    public event EventHandler<byte[]>? FrameCaptured;
    public event EventHandler<long>? LatencyUpdated;

    public SubtitleCoordinator(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationProvider azureProvider,
        ITranslationProvider mockProvider,
        ISettingsService settingsService,
        IFrameChangeDetector changeDetector)
    {
        _captureService = captureService;
        _ocrEngine = ocrEngine;
        _azureProvider = azureProvider;
        _mockProvider = mockProvider;
        _settingsService = settingsService;
        _changeDetector = changeDetector;
        _windowTracker = new WindowRegionTracker((ScreenCaptureService)captureService);

        _selectedRegion = _settingsService.Settings.LastRegion;
    }

    public void UpdateSelectedRegion(ScreenRegion region)
    {
        _selectedRegion = region;
        _settingsService.Settings.LastRegion = region;
        _changeDetector.Reset();

        if (_selectedWindow != null)
        {
            _windowTracker.SetTargetWindow(_selectedWindow, region);
        }
    }

    public void UpdateSelectedWindow(WindowInfo? window)
    {
        _selectedWindow = window;
        if (window != null && !_selectedRegion.IsEmpty)
        {
            _windowTracker.SetTargetWindow(window, _selectedRegion);
        }
        else
        {
            _windowTracker.Reset();
        }
    }

    public void Start()
    {
        if (State == SystemState.Running) return;

        State = SystemState.Running;
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessingLoopAsync(_cts.Token));
    }

    public void Pause()
    {
        State = SystemState.Paused;
    }

    public void Stop()
    {
        State = SystemState.Idle;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _changeDetector.Reset();
    }

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        int fps = Math.Clamp(_settingsService.Settings.CaptureFps, 1, 10);
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);

        string lastRecognizedText = string.Empty;

        while (!ct.IsCancellationRequested)
        {
            if (State == SystemState.Paused)
            {
                await Task.Delay(200, ct);
                continue;
            }

            var loopStopwatch = Stopwatch.StartNew();

            try
            {
                // Determine active capture region
                ScreenRegion currentRegion = _selectedRegion;

                if (_settingsService.Settings.RegionMode == RegionMode.FollowWindow && _selectedWindow != null)
                {
                    var (trackedRegion, isValid, _) = _windowTracker.GetCurrentRegion();
                    if (!isValid)
                    {
                        await Task.Delay(300, ct);
                        continue;
                    }
                    currentRegion = trackedRegion;
                }

                if (currentRegion.IsEmpty)
                {
                    await Task.Delay(300, ct);
                    continue;
                }

                // 1. Capture screen region
                var frameBytes = await _captureService.CaptureRegionAsync(currentRegion, ct);
                if (frameBytes == null || frameBytes.Length == 0)
                {
                    await Task.Delay(100, ct);
                    continue;
                }

                LatestFramePreview = frameBytes;
                FrameCaptured?.Invoke(this, frameBytes);

                // 2. Check frame difference
                if (!_changeDetector.HasFrameChanged(frameBytes, _settingsService.Settings.FrameDiffThreshold))
                {
                    // No visual change, skip OCR & translation to save resources
                    await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
                    continue;
                }

                // 3. Perform OCR
                var ocrResult = await _ocrEngine.RecognizeAsync(frameBytes, ct);
                LastOcrResult = ocrResult;

                if (!ocrResult.HasText || string.Equals(ocrResult.FullText, lastRecognizedText, StringComparison.Ordinal))
                {
                    await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
                    continue;
                }

                lastRecognizedText = ocrResult.FullText;

                // 4. Translate text
                var activeProvider = _settingsService.Settings.UseMockTranslation
                    ? _mockProvider
                    : _azureProvider;

                var transResult = await activeProvider.TranslateAsync(
                    ocrResult.FullText,
                    _settingsService.Settings.SourceLanguage,
                    _settingsService.Settings.TargetLanguages,
                    ct);

                LastTranslationResult = transResult;

                // 5. Update Latency metric
                long totalLatency = ocrResult.LatencyMs + transResult.LatencyMs;
                RecordLatency(totalLatency);

                // 6. Formulate Subtitle
                var subtitle = AssembleSubtitle(ocrResult.FullText, transResult);
                CurrentSubtitle = subtitle;
                SubtitleUpdated?.Invoke(this, subtitle);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // In case of transient network or OCR failure, sleep briefly and continue
                await Task.Delay(500, ct);
            }

            await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
        }
    }

    private SubtitleItem AssembleSubtitle(string originalText, TranslationResult trans)
    {
        string en = trans.GetTranslation(TargetLanguage.English);
        string vi = trans.GetTranslation(TargetLanguage.Vietnamese);
        string zh = trans.GetTranslation(TargetLanguage.Chinese);

        // If original text is already Chinese, fill zh with original text
        if (trans.DetectedLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ||
            originalText.Any(c => c >= 0x4E00 && c <= 0x9FFF))
        {
            zh = originalText;
        }

        // If original text is English
        if (trans.DetectedLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            en = originalText;
        }

        // If original text is Vietnamese
        if (trans.DetectedLanguage.Equals("vi", StringComparison.OrdinalIgnoreCase))
        {
            vi = originalText;
        }

        return new SubtitleItem
        {
            OriginalText = originalText,
            OriginalLanguageCode = trans.DetectedLanguage,
            ChineseText = zh,
            EnglishText = en,
            VietnameseText = vi,
            DisplayMode = _settingsService.Settings.DisplayMode,
            UpdatedAt = DateTime.UtcNow
        };
    }

    private void RecordLatency(long latency)
    {
        if (latency <= 0) return;

        _latencyHistory.Enqueue(latency);
        while (_latencyHistory.Count > MaxLatencySamples)
        {
            _latencyHistory.Dequeue();
        }

        AverageLatencyMs = (long)_latencyHistory.Average();
        LatencyUpdated?.Invoke(this, AverageLatencyMs);
    }

    private static async Task DelayRemainingAsync(TimeSpan elapsed, TimeSpan targetInterval, CancellationToken ct)
    {
        var remaining = targetInterval - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_processingTask != null)
        {
            try { await _processingTask; } catch { }
        }
    }
}
