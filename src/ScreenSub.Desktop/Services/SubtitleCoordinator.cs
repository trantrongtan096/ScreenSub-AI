using System.Diagnostics;
using System.Net.Http;
using System.Threading.Channels;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Core.Processing;
using ScreenSub.Translation;

namespace ScreenSub.Desktop.Services;

public sealed record TranslationRequestIdentity(
    string SessionId,
    long SessionEpoch,
    long SequenceId,
    long SettingsVersion,
    TranslationMode InputSource,
    ScreenRegion SourceRegion,
    string SourceLanguage,
    IReadOnlyList<TargetLanguage> TargetLanguages,
    DateTime CreatedAtUtc);

public sealed class SubtitleCoordinator : ISubtitleCoordinator
{
    private readonly IScreenCaptureService _captureService;
    private readonly IOcrEngine _ocrEngine;
    private readonly ITranslationProviderResolver _providerResolver;
    private readonly ISettingsService _settingsService;
    private readonly IFrameChangeDetector _changeDetector;
    private readonly WindowRegionTracker _windowTracker;

    private readonly IAudioCaptureService? _audioCaptureService;
    private readonly IVoiceActivityDetector? _vad;
    private readonly ISpeechRecognitionService? _speechService;

    private SystemState _state = SystemState.Idle;
    private SystemState _stateBeforeSelecting = SystemState.Idle;
    public string CurrentSessionId { get; private set; } = Guid.NewGuid().ToString("N");
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _inFlightTranslationCts;
    private readonly object _translationCtsLock = new();
    private readonly object _publishLock = new();
    private Task? _processingTask;
    private Task? _ocrTranslationTask;
    private Task? _speechWorkerTask;

    private sealed record OcrTranslationJob(
        TranslationRequestIdentity Identity,
        SubtitleAnalysisResult Analysis,
        long CaptureAndOcrElapsedMs,
        CancellationTokenSource JobCts,
        CancellationToken RequestCt);

    private Channel<OcrTranslationJob>? _ocrTranslationChannel;
    private Channel<SpeechSegment>? _speechChannel;
    private const int MaxSpeechQueueCapacity = 2;
    private const double MaxSpeechSegmentAgeMs = 8000;

    private ScreenRegion _selectedRegion;
    private WindowInfo? _selectedWindow;

    private long _currentSequenceId;
    private long _sessionEpoch = 1;
    private long _settingsVersion = 1;
    private long _minValidSequenceId;
    private long _latestOcrSequenceId;
    private long _latestSpeechSequenceId;
    private long _lastPublishedSequenceId;
    private long _droppedObsoleteCount;
    private long _rejectedStaleCount;

    private DateTime _lastTextSeenTime = DateTime.MinValue;
    private DateTime _lastOcrSeenTimeUtc = DateTime.MinValue;
    private string _lastStableOcrText = string.Empty;
    private string _lastSubmittedOcrText = string.Empty;
    private long _lastSubmittedSettingsVersion;
    private string _lastStableSpeechText = string.Empty;
    private bool _isOcrCurrentlyPresentOnScreen;
    private DateTime _lastOcrAttemptTime = DateTime.MinValue;
    private bool _hasOcrAttemptedUnchangedFrame;
    private int _unchangedEmptyOrFailureRetries;
    private int _engineNotReadyRetries;
    private bool _needsRetryAfterEmptyOrFailure;
    private const int MaxUnchangedEmptyOrFailedOcrRetries = 2;
    private const int MaxEngineNotReadyRetries = 3;
    private const int UnchangedRetryCooldownMs = 450;
    private int _consecutiveBlankFrames = 0;
    private long _frameSkippedUnchangedCount;
    private long _duplicateSuppressedCount;
    private long _ocrEmptyOrFailureRetryCount;

    public long SettingsVersion => Interlocked.Read(ref _settingsVersion);
    public long SessionEpoch => Interlocked.Read(ref _sessionEpoch);
    public long DroppedObsoleteCount => Interlocked.Read(ref _droppedObsoleteCount);
    public long RejectedStaleCount => Interlocked.Read(ref _rejectedStaleCount);
    public long FrameSkippedUnchangedCount => Interlocked.Read(ref _frameSkippedUnchangedCount);
    public long DuplicateSuppressedCount => Interlocked.Read(ref _duplicateSuppressedCount);
    public long OcrEmptyOrFailureRetryCount => Interlocked.Read(ref _ocrEmptyOrFailureRetryCount);
    public long LastQueueWaitMs { get; private set; } = 0;
    public long LastSpeechQueueWaitMs { get; private set; } = 0;

    public long CaptureFrameCount { get; private set; } = 0;
    public long OcrAttemptCount { get; private set; } = 0;
    public long OcrSuccessCount { get; private set; } = 0;

    private readonly Queue<long> _latencyHistory = new();
    private readonly Queue<DateTime> _fpsTimestamps = new();
    private const int MaxLatencySamples = 10;
    private readonly Queue<SubtitleItem> _conversationContext = new();

    public IReadOnlyList<SubtitleItem> RecentConversationContext
    {
        get
        {
            lock (_conversationContext)
            {
                return _conversationContext.ToArray();
            }
        }
    }

    public TranslationMode Mode { get; private set; } = TranslationMode.ScreenOcr;

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

    public long AverageLatencyMs { get; private set; } = 0; // 0 indicates unmeasured
    public int CurrentFps { get; private set; } = 0;
    public long LastCaptureLatencyMs { get; private set; } = 0;
    public long LastOcrLatencyMs { get; private set; } = 0;
    public long LastTranslationLatencyMs { get; private set; } = 0;
    public long LastOverlayUpdateLatencyMs { get; set; } = 0;
    public long LastEndToEndLatencyMs { get; private set; } = 0;
    public int OcrQueueLength { get; private set; } = 0;
    public double CacheHitRate =>
        (_providerResolver.GetProvider(TranslationEngineMode.Online) as AzureTranslatorProvider)?.Cache.HitRate
        ?? (_providerResolver.GetProvider(TranslationEngineMode.Online) as GeminiTranslationProvider)?.Cache.HitRate
        ?? 0.0;
    public string ActiveProviderBadgeText => _providerResolver.ActiveProviderBadgeText;

    public IReadOnlyList<AudioDeviceInfo> OutputAudioDevices => _audioCaptureService?.GetOutputDevices() ?? Array.Empty<AudioDeviceInfo>();
    public AudioDeviceInfo? SelectedAudioDevice => _audioCaptureService?.SelectedDevice;
    public float AudioPeakLevel => _audioCaptureService?.CurrentPeakLevel ?? 0f;
    public float AudioRmsLevel => _audioCaptureService?.CurrentRmsLevel ?? 0f;
    public string SpeechStatusText => _speechService?.StatusMessage ?? "Chưa khởi động";
    public string LastRecognizedSpeechText { get; private set; } = string.Empty;
    public string LastSpeechLanguage { get; private set; } = "auto";
    public long LastSpeechLatencyMs { get; private set; } = 0;

    public CaptureFrameResult LastCaptureDiagnostics { get; private set; } = new();
    public OcrResult LastOcrResult { get; private set; } = OcrResult.Empty;
    public string LastOcrRawText { get; private set; } = string.Empty;
    public string LastNormalizedText { get; private set; } = string.Empty;
    public TranslationResult LastTranslationResult { get; private set; } = TranslationResult.Empty;
    public SubtitleItem CurrentSubtitle { get; private set; } = SubtitleItem.Empty;
    public byte[]? LatestFramePreview { get; private set; }

    private string? _overrideTranslationStatus;
    public string OcrStatusText => _ocrEngine.StatusMessage;
    public string TranslationStatusText
    {
        get => _overrideTranslationStatus ?? _providerResolver.ResolveActiveProvider().StatusMessage;
        private set => _overrideTranslationStatus = value;
    }
    public string PipelineStatusText { get; private set; } = "Sẵn sàng";
    public bool IsMockMode => _settingsService.Settings.UseMockTranslation;
    public bool HasActiveCapture => LatestFramePreview != null && LatestFramePreview.Length > 0;

    public event EventHandler<SystemState>? StateChanged;
    public event EventHandler<(string Text, string LanguageCode)>? OcrTextRecognized;
    public event EventHandler<SubtitleItem>? SubtitleUpdated;
    public event EventHandler<byte[]>? FrameCaptured;
    public event EventHandler<long>? LatencyUpdated;
    public event EventHandler<string>? StatusUpdated;
    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    public event EventHandler<SpeechRecognitionResult>? SpeechRecognized;

    public SubtitleCoordinator(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationProviderResolver providerResolver,
        ISettingsService settingsService,
        IFrameChangeDetector changeDetector,
        IAudioCaptureService? audioCaptureService = null,
        IVoiceActivityDetector? vad = null,
        ISpeechRecognitionService? speechService = null)
    {
        _captureService = captureService;
        _ocrEngine = ocrEngine;
        _providerResolver = providerResolver;
        _settingsService = settingsService;
        _changeDetector = changeDetector;
        _windowTracker = new WindowRegionTracker(captureService as ScreenCaptureService ?? new ScreenCaptureService());

        _audioCaptureService = audioCaptureService;
        _vad = vad;
        _speechService = speechService;
        Mode = _settingsService.Settings.TranslationMode;

        _selectedRegion = _settingsService.Settings.LastRegion;
        UpdateInitialStatusMessages();

        if (_providerResolver.GetProvider(TranslationEngineMode.Offline) is LocalTranslationProvider localProv)
        {
            localProv.Engine.StateChanged += (_, _) =>
            {
                if (_providerResolver.ResolveActiveProvider() == localProv)
                {
                    TranslationStatusText = localProv.StatusMessage;
                    StatusUpdated?.Invoke(this, PipelineStatusText);
                }
            };
        }

        // Wire Audio & VAD events
        if (_audioCaptureService != null)
        {
            _audioCaptureService.AudioLevelChanged += (s, e) => AudioLevelChanged?.Invoke(this, e);
            _audioCaptureService.AudioDataAvailable += (s, e) =>
            {
                if (Mode != TranslationMode.ScreenOcr && State == SystemState.Running)
                {
                    _vad?.ProcessAudioChunk(e.Pcm16Data, e.SampleRate);
                }
            };
        }

        if (_vad != null)
        {
            _vad.SpeechSegmentReady += (s, seg) =>
            {
                if (Mode != TranslationMode.ScreenOcr && State == SystemState.Running)
                {
                    var ch = _speechChannel;
                    ch?.Writer.TryWrite(seg);
                }
            };
        }
    }

    private Channel<OcrTranslationJob> CreateBoundedOcrChannel() =>
        Channel.CreateBounded<OcrTranslationJob>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            dropped =>
            {
                Interlocked.Increment(ref _droppedObsoleteCount);
                lock (_translationCtsLock)
                {
                    if (ReferenceEquals(_inFlightTranslationCts, dropped.JobCts))
                    {
                        _inFlightTranslationCts = null;
                    }
                }
                try { dropped.JobCts.Cancel(); } catch { }
                try { dropped.JobCts.Dispose(); } catch { }
            });

    private Channel<SpeechSegment> CreateBoundedSpeechChannel() =>
        Channel.CreateBounded<SpeechSegment>(
            new BoundedChannelOptions(MaxSpeechQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            },
            _ => Interlocked.Increment(ref _droppedObsoleteCount));

    private void CancelInFlightOcrTranslation()
    {
        lock (_translationCtsLock)
        {
            try { _inFlightTranslationCts?.Cancel(); } catch { }
            _inFlightTranslationCts = null;
        }
    }

    private static void RetireSessionCts(CancellationTokenSource? retiringCts, params Task?[] activeTasks)
    {
        if (retiringCts == null) return;
        try { retiringCts.Cancel(); } catch { }

        var pending = activeTasks.Where(t => t != null).Cast<Task>().ToArray();
        if (pending.Length == 0)
        {
            try { retiringCts.Dispose(); } catch { }
            return;
        }

        _ = Task.WhenAll(pending).ContinueWith(
            _ => { try { retiringCts.Dispose(); } catch { } },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private (CancellationTokenSource JobCts, CancellationToken Token) CreateInFlightOcrCts(CancellationToken sessionCt)
    {
        lock (_translationCtsLock)
        {
            try { _inFlightTranslationCts?.Cancel(); } catch { }
            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCt);
            _inFlightTranslationCts = jobCts;
            return (jobCts, jobCts.Token);
        }
    }

    private long AdvanceMinValidSequence()
    {
        long threshold = Interlocked.Increment(ref _currentSequenceId);
        Interlocked.Exchange(ref _minValidSequenceId, threshold);
        return threshold;
    }

    public SubtitleCoordinator(
        IScreenCaptureService captureService,
        IOcrEngine ocrEngine,
        ITranslationProvider azureProvider,
        ITranslationProvider mockProvider,
        ISettingsService settingsService,
        IFrameChangeDetector changeDetector,
        IAudioCaptureService? audioCaptureService = null,
        IVoiceActivityDetector? vad = null,
        ISpeechRecognitionService? speechService = null)
        : this(
            captureService,
            ocrEngine,
            CreateFallbackResolver(azureProvider, mockProvider, settingsService),
            settingsService,
            changeDetector,
            audioCaptureService,
            vad,
            speechService)
    {
    }

    private static ITranslationProviderResolver CreateFallbackResolver(
        ITranslationProvider azureProvider,
        ITranslationProvider mockProvider,
        ISettingsService settingsService)
    {
        var localEngine = new LocalNeuralTranslationEngine(settingsService);
        var cache = (azureProvider as AzureTranslatorProvider)?.Cache ?? new TranslationCache();
        var localProvider = new LocalTranslationProvider(localEngine, cache, settingsService);
        var az = azureProvider as AzureTranslatorProvider ?? new AzureTranslatorProvider(new HttpClient(), settingsService, cache);
        var mk = mockProvider as MockTranslationProvider ?? new MockTranslationProvider();
        return new TranslationProviderResolver(settingsService, localProvider, az, mk);
    }

    private void UpdateInitialStatusMessages()
    {
        var active = _providerResolver.ResolveActiveProvider();
        TranslationStatusText = active.StatusMessage;
    }

    public void UpdateSelectedRegion(ScreenRegion region)
    {
        _selectedRegion = region;
        _settingsService.Settings.LastRegion = region;
        _changeDetector.Reset();
        Interlocked.Increment(ref _settingsVersion);
        AdvanceMinValidSequence();
        CancelInFlightOcrTranslation();
        _lastStableOcrText = string.Empty;
        _lastSubmittedOcrText = string.Empty;
        _lastOcrSeenTimeUtc = DateTime.MinValue;
        _isOcrCurrentlyPresentOnScreen = false;
        _hasOcrAttemptedUnchangedFrame = false;
        _unchangedEmptyOrFailureRetries = 0;
        _engineNotReadyRetries = 0;
        _needsRetryAfterEmptyOrFailure = false;

        if (_selectedWindow != null)
        {
            _windowTracker.SetTargetWindow(_selectedWindow, region);
        }

        // When region is reset/cleared, clear subtitle
        if (region.IsEmpty)
        {
            ClearSubtitle();
        }
    }

    public void UpdateSelectedWindow(WindowInfo? window)
    {
        _selectedWindow = window;
        Interlocked.Increment(ref _settingsVersion);
        AdvanceMinValidSequence();
        CancelInFlightOcrTranslation();
        if (window != null && !_selectedRegion.IsEmpty)
        {
            _windowTracker.SetTargetWindow(window, _selectedRegion);
        }
        else
        {
            _windowTracker.Reset();
        }
    }

    public void SetTranslationMode(TranslationMode mode)
    {
        if (Mode == mode) return;

        bool wasRunning = State == SystemState.Running;
        if (wasRunning)
        {
            Pause();
        }

        Mode = mode;
        _settingsService.Settings.TranslationMode = mode;
        _settingsService.SaveSettingsAsync();
        Interlocked.Increment(ref _settingsVersion);
        AdvanceMinValidSequence();

        if (mode == TranslationMode.AudioRealtime)
        {
            PipelineStatusText = "Chế độ: Dịch âm thanh (Audio Realtime)";
        }
        else if (mode == TranslationMode.Hybrid)
        {
            PipelineStatusText = "Chế độ: Kết hợp (OCR + Audio Hybrid)";
        }
        else
        {
            PipelineStatusText = "Chế độ: Nhận diện màn hình (Screen OCR)";
        }

        StatusUpdated?.Invoke(this, PipelineStatusText);

        if (wasRunning)
        {
            Start();
        }
    }

    public void ResetTranslationCacheAndRetranslate()
    {
        _overrideTranslationStatus = null;
        Interlocked.Increment(ref _settingsVersion);
        AdvanceMinValidSequence();
        _lastStableOcrText = string.Empty;
        _lastSubmittedOcrText = string.Empty;
        _lastStableSpeechText = string.Empty;
        _lastOcrSeenTimeUtc = DateTime.MinValue;
        _isOcrCurrentlyPresentOnScreen = false;
        _hasOcrAttemptedUnchangedFrame = false;
        _unchangedEmptyOrFailureRetries = 0;
        _engineNotReadyRetries = 0;
        _needsRetryAfterEmptyOrFailure = false;
        CancelInFlightOcrTranslation();
        ClearSubtitle();
    }

    public async Task<(bool Success, string Message)> TestOnlineConnectionAsync(CancellationToken ct = default)
    {
        var result = await _providerResolver.TestOnlineConnectionAsync(ct);
        _overrideTranslationStatus = null;
        StatusUpdated?.Invoke(this, PipelineStatusText);
        return result;
    }

    public void RefreshAudioDevices()
    {
        _audioCaptureService?.GetOutputDevices();
    }

    public void SetSelectedAudioDevice(AudioDeviceInfo? device)
    {
        _audioCaptureService?.SetSelectedDevice(device);
        _settingsService.Settings.SelectedAudioDeviceId = device?.Id;
        _settingsService.SaveSettingsAsync();
    }

    public void NotifySelectingRegion()
    {
        _stateBeforeSelecting = State;
        State = SystemState.SelectingRegion;
        PipelineStatusText = "Đang chọn vùng dịch (Ctrl + Alt + Q / ESC để hủy)...";
        StatusUpdated?.Invoke(this, PipelineStatusText);
    }

    public void CancelSelectingRegion()
    {
        if (State != SystemState.SelectingRegion) return;

        if (_stateBeforeSelecting == SystemState.Translating)
        {
            State = SystemState.Translating;
            PipelineStatusText = "Đã hủy chọn vùng. Tiếp tục phiên dịch hiện tại.";
        }
        else if (_stateBeforeSelecting == SystemState.Paused)
        {
            State = SystemState.Paused;
            PipelineStatusText = "Tạm dừng (Paused)";
        }
        else
        {
            State = _stateBeforeSelecting == SystemState.Stopped ? SystemState.Stopped : SystemState.Idle;
            PipelineStatusText = "Đã hủy chọn vùng";
        }
        StatusUpdated?.Invoke(this, PipelineStatusText);
    }

    public void UpdateSelectedRegionAndAutoStart(ScreenRegion region)
    {
        if (region.IsEmpty) return;

        Interlocked.Increment(ref _sessionEpoch);
        Interlocked.Increment(ref _settingsVersion);
        AdvanceMinValidSequence();
        CurrentSessionId = Guid.NewGuid().ToString("N");
        CancelInFlightOcrTranslation();

        _changeDetector.Reset();
        _lastStableOcrText = string.Empty;
        _lastSubmittedOcrText = string.Empty;
        _lastStableSpeechText = string.Empty;
        _lastTextSeenTime = DateTime.MinValue;
        _lastOcrSeenTimeUtc = DateTime.MinValue;
        _isOcrCurrentlyPresentOnScreen = false;
        _lastOcrAttemptTime = DateTime.MinValue;
        _hasOcrAttemptedUnchangedFrame = false;
        _unchangedEmptyOrFailureRetries = 0;
        _engineNotReadyRetries = 0;
        _needsRetryAfterEmptyOrFailure = false;
        _consecutiveBlankFrames = 0;

        ClearSubtitle();

        _selectedRegion = region;
        if (_selectedWindow != null)
        {
            _windowTracker.SetTargetWindow(_selectedWindow, region);
        }

        State = SystemState.Starting;
        PipelineStatusText = $"Khởi động nhận diện vùng {region.FormattedSize}...";
        StatusUpdated?.Invoke(this, PipelineStatusText);

        var retiringCts = _cts;
        _cts = null;
        RetireSessionCts(retiringCts, _processingTask, _ocrTranslationTask, _speechWorkerTask);

        _ocrTranslationChannel?.Writer.TryComplete();
        _speechChannel?.Writer.TryComplete();
        var ocrChannel = CreateBoundedOcrChannel();
        var speechChannel = CreateBoundedSpeechChannel();
        _ocrTranslationChannel = ocrChannel;
        _speechChannel = speechChannel;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        State = SystemState.Translating;
        PipelineStatusText = Mode == TranslationMode.AudioRealtime
            ? "Đang lắng nghe âm thanh (Listening)..."
            : "Đang nhận diện và dịch (Translating)...";
        StatusUpdated?.Invoke(this, PipelineStatusText);

        _processingTask = Task.Run(() => ProcessingLoopAsync(ct));
        _ocrTranslationTask = Task.Run(() => OcrTranslationWorkerLoopAsync(ocrChannel.Reader, ct));

        if (Mode != TranslationMode.ScreenOcr)
        {
            if (_speechService != null && !_speechService.IsModelLoaded)
            {
                _ = Task.Run(() => _speechService.InitializeAsync(ct), ct);
            }
            _audioCaptureService?.Start();
            _speechWorkerTask = Task.Run(() => SpeechWorkerLoopAsync(speechChannel.Reader, ct));
        }
    }

    public void Start()
    {
        if (State == SystemState.Translating) return;

        if (State == SystemState.Paused)
        {
            State = SystemState.Translating;
            if (Mode != TranslationMode.ScreenOcr)
            {
                _audioCaptureService?.Start();
            }
            PipelineStatusText = Mode == TranslationMode.AudioRealtime
                ? "Đang lắng nghe âm thanh (Listening)..."
                : "Đang chạy (Running)";
            StatusUpdated?.Invoke(this, PipelineStatusText);
            return;
        }

        Interlocked.Increment(ref _sessionEpoch);
        AdvanceMinValidSequence();
        CurrentSessionId = Guid.NewGuid().ToString("N");
        State = SystemState.Starting;
        PipelineStatusText = Mode == TranslationMode.AudioRealtime
            ? "Đang khởi động thu âm thanh..."
            : "Đang khởi động nhận diện màn hình...";
        StatusUpdated?.Invoke(this, PipelineStatusText);

        _ocrTranslationChannel?.Writer.TryComplete();
        _speechChannel?.Writer.TryComplete();
        var ocrChannel = CreateBoundedOcrChannel();
        var speechChannel = CreateBoundedSpeechChannel();
        _ocrTranslationChannel = ocrChannel;
        _speechChannel = speechChannel;

        _changeDetector.Reset();
        _hasOcrAttemptedUnchangedFrame = false;
        _unchangedEmptyOrFailureRetries = 0;
        _engineNotReadyRetries = 0;
        _needsRetryAfterEmptyOrFailure = false;

        State = SystemState.Translating;
        PipelineStatusText = Mode == TranslationMode.AudioRealtime
            ? "Đang lắng nghe âm thanh (Listening)..."
            : "Đang chạy (Running)";
        StatusUpdated?.Invoke(this, PipelineStatusText);

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // 1. Start Screen Capture + OCR producer and Bounded Latest-Wins translation consumer
        _processingTask = Task.Run(() => ProcessingLoopAsync(ct));
        _ocrTranslationTask = Task.Run(() => OcrTranslationWorkerLoopAsync(ocrChannel.Reader, ct));

        // 2. Start Audio Capture and Speech Recognition Worker if in Audio or Hybrid mode
        if (Mode != TranslationMode.ScreenOcr)
        {
            if (_speechService != null && !_speechService.IsModelLoaded)
            {
                _ = Task.Run(() => _speechService.InitializeAsync(ct), ct);
            }
            _audioCaptureService?.Start();
            _speechWorkerTask = Task.Run(() => SpeechWorkerLoopAsync(speechChannel.Reader, ct));
        }
    }

    public void Pause()
    {
        if (State != SystemState.Translating && State != SystemState.Starting) return;
        AdvanceMinValidSequence();
        State = SystemState.Paused;
        _audioCaptureService?.Pause();
        CancelInFlightOcrTranslation();
        PipelineStatusText = "Tạm dừng (Paused)";
        StatusUpdated?.Invoke(this, PipelineStatusText);
    }

    public void Stop()
    {
        Interlocked.Increment(ref _sessionEpoch);
        AdvanceMinValidSequence();
        CurrentSessionId = Guid.NewGuid().ToString("N");
        State = SystemState.Stopped;
        var retiringCts = _cts;
        _cts = null;
        RetireSessionCts(retiringCts, _processingTask, _ocrTranslationTask, _speechWorkerTask);

        _audioCaptureService?.Stop();
        _vad?.Reset();

        CancelInFlightOcrTranslation();

        _ocrTranslationChannel?.Writer.TryComplete();
        _speechChannel?.Writer.TryComplete();
        if (_ocrTranslationChannel != null)
        {
            while (_ocrTranslationChannel.Reader.TryRead(out var drainedJob))
            {
                try { drainedJob.JobCts.Cancel(); } catch { }
                try { drainedJob.JobCts.Dispose(); } catch { }
            }
        }
        if (_speechChannel != null)
        {
            while (_speechChannel.Reader.TryRead(out _)) { }
        }

        _changeDetector.Reset();
        _lastStableOcrText = string.Empty;
        _lastSubmittedOcrText = string.Empty;
        _lastStableSpeechText = string.Empty;
        _lastTextSeenTime = DateTime.MinValue;
        _lastOcrSeenTimeUtc = DateTime.MinValue;
        _isOcrCurrentlyPresentOnScreen = false;
        _lastOcrAttemptTime = DateTime.MinValue;
        _hasOcrAttemptedUnchangedFrame = false;
        _unchangedEmptyOrFailureRetries = 0;
        _engineNotReadyRetries = 0;
        _needsRetryAfterEmptyOrFailure = false;
        _consecutiveBlankFrames = 0;

        // Reset latency & FPS
        _latencyHistory.Clear();
        _fpsTimestamps.Clear();
        lock (_conversationContext)
        {
            _conversationContext.Clear();
        }
        AverageLatencyMs = 0;
        CurrentFps = 0;
        LastCaptureLatencyMs = 0;
        LastOcrLatencyMs = 0;
        LastTranslationLatencyMs = 0;
        LastSpeechLatencyMs = 0;
        LastEndToEndLatencyMs = 0;
        LastQueueWaitMs = 0;
        LastSpeechQueueWaitMs = 0;
        OcrQueueLength = 0;
        LatencyUpdated?.Invoke(this, 0);

        // Clear subtitle on dashboard and overlay
        ClearSubtitle();

        // Clear preview
        LatestFramePreview = null;
        FrameCaptured?.Invoke(this, Array.Empty<byte>());

        PipelineStatusText = "Đã dừng (Stopped)";
        StatusUpdated?.Invoke(this, PipelineStatusText);
    }

    private void ClearSubtitle()
    {
        lock (_publishLock)
        {
            CurrentSubtitle = SubtitleItem.Empty;
            SubtitleUpdated?.Invoke(this, SubtitleItem.Empty);
        }
    }

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        int fps = Math.Clamp(_settingsService.Settings.CaptureFps, 1, 10);
        var interval = TimeSpan.FromMilliseconds(1000.0 / fps);
        int blankDebounceMs = Math.Max(500, _settingsService.Settings.StabilityDebounceMs * 4);

        while (!ct.IsCancellationRequested)
        {
            if (State == SystemState.Paused)
            {
                await Task.Delay(200, ct);
                continue;
            }

            if (Mode == TranslationMode.AudioRealtime)
            {
                // In AudioRealtime mode, screen capture is bypassed.
                // Subtitles are produced by SpeechWorkerLoopAsync.
                CheckSubtitleTimeout(blankDebounceMs);
                await Task.Delay(250, ct);
                continue;
            }

            var loopStopwatch = Stopwatch.StartNew();

            try
            {
                // 1. Determine active capture region
                ScreenRegion currentRegion = _selectedRegion;

                if (_settingsService.Settings.RegionMode == RegionMode.FollowWindow && _selectedWindow != null)
                {
                    var (trackedRegion, isValid, statusMsg) = _windowTracker.GetCurrentRegion();
                    if (!isValid)
                    {
                        PipelineStatusText = statusMsg;
                        StatusUpdated?.Invoke(this, statusMsg);
                        await Task.Delay(400, ct);
                        continue;
                    }
                    currentRegion = trackedRegion;
                }

                if (currentRegion.IsEmpty)
                {
                    PipelineStatusText = "Chưa chọn vùng nhận diện";
                    StatusUpdated?.Invoke(this, PipelineStatusText);
                    await Task.Delay(400, ct);
                    continue;
                }

                // 2. Capture screen region with diagnostics & self-capture protection
                var captureSw = Stopwatch.StartNew();
                var captureResult = await _captureService.CaptureWithDiagnosticsAsync(
                    currentRegion,
                    _selectedWindow,
                    _settingsService.Settings.RegionMode,
                    _settingsService.Settings.CaptureSource,
                    ct);
                captureSw.Stop();

                if (captureResult == null)
                {
                    var legacySw = Stopwatch.StartNew();
                    var legacyBytes = await _captureService.CaptureRegionAsync(currentRegion, ct);
                    legacySw.Stop();
                    captureResult = new CaptureFrameResult
                    {
                        ImageBytes = legacyBytes ?? Array.Empty<byte>(),
                        ErrorKind = (legacyBytes != null && legacyBytes.Length > 0) ? CaptureErrorKind.None : CaptureErrorKind.ApiError,
                        ErrorMessage = (legacyBytes != null && legacyBytes.Length > 0) ? string.Empty : "Không thể capture màn hình (có thể do DRM hoặc cửa sổ được bảo vệ)",
                        BackendName = "ScreenCapture",
                        RequestedRegion = currentRegion,
                        CropWidth = currentRegion.Width,
                        CropHeight = currentRegion.Height,
                        CaptureLatencyMs = legacySw.ElapsedMilliseconds
                    };
                }

                LastCaptureDiagnostics = captureResult;
                LastCaptureLatencyMs = captureResult.CaptureLatencyMs > 0 ? captureResult.CaptureLatencyMs : captureSw.ElapsedMilliseconds;

                if (!captureResult.IsSuccess)
                {
                    PipelineStatusText = !string.IsNullOrWhiteSpace(captureResult.ErrorMessage)
                        ? captureResult.ErrorMessage
                        : "Không thể capture màn hình (có thể do DRM hoặc cửa sổ được bảo vệ)";
                    StatusUpdated?.Invoke(this, PipelineStatusText);
                    await Task.Delay(250, ct);
                    continue;
                }

                if (_selectedWindow == null && captureResult.SourceHwnd != nint.Zero)
                {
                    var autoWin = _captureService.GetWindowInfo(captureResult.SourceHwnd);
                    if (autoWin != null)
                    {
                        _selectedWindow = autoWin;
                        _windowTracker.SetTargetWindow(autoWin, currentRegion);
                    }
                }

                var frameBytes = captureResult.ImageBytes ?? Array.Empty<byte>();

                // Track FPS
                var now = DateTime.UtcNow;
                _fpsTimestamps.Enqueue(now);
                while (_fpsTimestamps.Count > 0 && (now - _fpsTimestamps.Peek()).TotalSeconds > 1.0)
                {
                    _fpsTimestamps.Dequeue();
                }
                CurrentFps = _fpsTimestamps.Count;

                LatestFramePreview = frameBytes;
                FrameCaptured?.Invoke(this, frameBytes);
                CaptureFrameCount++;

                // 3. Check frame difference with bounded retry policy (prevents infinite polling on static frames while recovering transient empty/failed OCR)
                bool frameChanged = _changeDetector.HasFrameChanged(frameBytes, _settingsService.Settings.FrameDiffThreshold);
                if (frameChanged)
                {
                    _hasOcrAttemptedUnchangedFrame = false;
                    _unchangedEmptyOrFailureRetries = 0;
                    _engineNotReadyRetries = 0;
                    _needsRetryAfterEmptyOrFailure = false;
                }

                bool engineNotYetReady = _ocrEngine.Status != ServiceStatus.Running;
                bool canInitialAttempt = !_hasOcrAttemptedUnchangedFrame;
                bool canNotReadyRetry = engineNotYetReady && _engineNotReadyRetries < MaxEngineNotReadyRetries;
                bool canEmptyOrFailureRetry = !engineNotYetReady
                    && _needsRetryAfterEmptyOrFailure
                    && _unchangedEmptyOrFailureRetries < MaxUnchangedEmptyOrFailedOcrRetries;

                bool forceRetryOcr = !frameChanged
                    && (canInitialAttempt || canNotReadyRetry || canEmptyOrFailureRetry)
                    && (DateTime.UtcNow - _lastOcrAttemptTime).TotalMilliseconds >= UnchangedRetryCooldownMs;

                if (!frameChanged && !forceRetryOcr)
                {
                    Interlocked.Increment(ref _frameSkippedUnchangedCount);
                    if (_isOcrCurrentlyPresentOnScreen && _consecutiveBlankFrames == 0 && !string.IsNullOrWhiteSpace(_lastStableOcrText))
                    {
                        _lastOcrSeenTimeUtc = DateTime.UtcNow;
                    }
                    // No visual change: subtitle is still actively on screen! Keep subtitle visible.
                    await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
                    continue;
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (!frameChanged && !canInitialAttempt)
                {
                    if (canNotReadyRetry)
                    {
                        _engineNotReadyRetries++;
                    }
                    else if (canEmptyOrFailureRetry)
                    {
                        _unchangedEmptyOrFailureRetries++;
                        Interlocked.Increment(ref _ocrEmptyOrFailureRetryCount);
                    }
                }

                // 4. Perform OCR on real image with stale epoch/session/region guard
                long ocrStartEpoch = SessionEpoch;
                string ocrStartSessionId = CurrentSessionId;
                _lastOcrAttemptTime = DateTime.UtcNow;
                _hasOcrAttemptedUnchangedFrame = true;
                _needsRetryAfterEmptyOrFailure = true;
                OcrAttemptCount++;
                PipelineStatusText = "Đang nhận diện (Recognizing)...";
                StatusUpdated?.Invoke(this, PipelineStatusText);

                var ocrSw = Stopwatch.StartNew();
                var ocrResult = await _ocrEngine.RecognizeAsync(frameBytes, ct);
                ocrSw.Stop();

                if (ct.IsCancellationRequested
                    || State != SystemState.Translating
                    || ocrStartEpoch != SessionEpoch
                    || !string.Equals(ocrStartSessionId, CurrentSessionId, StringComparison.Ordinal)
                    || !Equals(currentRegion, _selectedRegion))
                {
                    Interlocked.Increment(ref _rejectedStaleCount);
                    await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
                    continue;
                }

                LastOcrResult = ocrResult;
                LastOcrLatencyMs = ocrResult.LatencyMs > 0 ? ocrResult.LatencyMs : ocrSw.ElapsedMilliseconds;

                // Handle blank frame / transient empty OCR (Trường hợp C + bounded retry)
                if (!ocrResult.HasText)
                {
                    OnBlankFrameDetected(frameChanged);
                    if (!CurrentSubtitle.HasContent)
                    {
                        PipelineStatusText = "Đang chờ phụ đề (Waiting for subtitles)";
                        StatusUpdated?.Invoke(this, PipelineStatusText);
                    }
                    await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
                    continue;
                }

                _consecutiveBlankFrames = 0;
                _isOcrCurrentlyPresentOnScreen = true;
                OcrSuccessCount++;
                var ocrSeenNow = DateTime.UtcNow;
                _lastTextSeenTime = ocrSeenNow;
                _lastOcrSeenTimeUtc = ocrSeenNow;
                LastOcrRawText = ocrResult.FullText;

                // Analyze and segment multi-line / mixed-language OCR blocks
                var analysis = SubtitleTextProcessor.AnalyzeOcrText(
                    ocrResult.FullText,
                    _settingsService.Settings.SourceLanguage);

                if (string.IsNullOrWhiteSpace(analysis.PrimarySourceText))
                {
                    OnBlankFrameDetected(frameChanged);
                    if (!CurrentSubtitle.HasContent)
                    {
                        PipelineStatusText = "Đang chờ phụ đề (Waiting for subtitles)";
                        StatusUpdated?.Invoke(this, PipelineStatusText);
                    }
                    await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
                    continue;
                }

                _needsRetryAfterEmptyOrFailure = false;
                _unchangedEmptyOrFailureRetries = 0;
                _lastStableOcrText = analysis.PrimarySourceText;

                LastNormalizedText = analysis.PrimarySourceText;
                OcrTextRecognized?.Invoke(this, (analysis.PrimarySourceText, analysis.DetectedPrimaryLanguage));

                // Fuzzy deduplication against both last published subtitle AND last submitted in-flight OCR text
                // (prevents redundant cancellation/re-translation when Capture+OCR runs faster than translation)
                long currentSettingsVer = SettingsVersion;
                string compareBasis = !string.IsNullOrEmpty(_lastSubmittedOcrText) && _lastSubmittedSettingsVersion == currentSettingsVer
                    ? _lastSubmittedOcrText
                    : string.Empty;

                bool isDuplicate = string.Equals(analysis.PrimarySourceText, compareBasis, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(compareBasis) && SubtitleTextProcessor.CalculateSimilarity(analysis.PrimarySourceText, compareBasis) >= 0.88)
                    || (!string.IsNullOrEmpty(compareBasis)
                        && analysis.PrimarySourceText.Length < compareBasis.Length * 0.65
                        && compareBasis.Contains(analysis.PrimarySourceText, StringComparison.OrdinalIgnoreCase)
                        && (DateTime.UtcNow - _lastTextSeenTime).TotalMilliseconds < 1500);

                if (isDuplicate && _lastSubmittedSettingsVersion == currentSettingsVer)
                {
                    Interlocked.Increment(ref _duplicateSuppressedCount);
                    await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
                    continue;
                }

                // Latest Request Wins: atomically verify session epoch before replacing in-flight CTS & queueing
                OcrTranslationJob job;
                lock (_translationCtsLock)
                {
                    if (ct.IsCancellationRequested
                        || State != SystemState.Translating
                        || ocrStartEpoch != SessionEpoch
                        || !string.Equals(ocrStartSessionId, CurrentSessionId, StringComparison.Ordinal)
                        || !Equals(currentRegion, _selectedRegion))
                    {
                        Interlocked.Increment(ref _rejectedStaleCount);
                        continue;
                    }

                    long sequenceId = Interlocked.Increment(ref _currentSequenceId);
                    Interlocked.Exchange(ref _latestOcrSequenceId, sequenceId);

                    var identity = new TranslationRequestIdentity(
                        SessionId: ocrStartSessionId,
                        SessionEpoch: ocrStartEpoch,
                        SequenceId: sequenceId,
                        SettingsVersion: currentSettingsVer,
                        InputSource: TranslationMode.ScreenOcr,
                        SourceRegion: currentRegion,
                        SourceLanguage: analysis.DetectedPrimaryLanguage,
                        TargetLanguages: _settingsService.Settings.TargetLanguages.ToArray(),
                        CreatedAtUtc: DateTime.UtcNow);

                    var (jobCts, transToken) = CreateInFlightOcrCts(ct);
                    _lastSubmittedOcrText = analysis.PrimarySourceText;
                    _lastSubmittedSettingsVersion = currentSettingsVer;

                    long captureOcrMs = Math.Max(1, loopStopwatch.ElapsedMilliseconds);
                    job = new OcrTranslationJob(identity, analysis, captureOcrMs, jobCts, transToken);
                }

                var ocrCh = _ocrTranslationChannel;
                if (ocrCh != null && ocrCh.Writer.TryWrite(job))
                {
                    OcrQueueLength = 1;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                PipelineStatusText = $"Lỗi xử lý: {ex.Message}";
                StatusUpdated?.Invoke(this, PipelineStatusText);
                await Task.Delay(500, ct);
            }

            await DelayRemainingAsync(loopStopwatch.Elapsed, interval, ct);
        }
    }

    private bool IsOcrJobStale(TranslationRequestIdentity id, CancellationToken requestCt)
    {
        return requestCt.IsCancellationRequested
            || State != SystemState.Translating
            || id.SessionEpoch != SessionEpoch
            || id.SettingsVersion != SettingsVersion
            || !string.Equals(id.SessionId, CurrentSessionId, StringComparison.Ordinal)
            || id.SequenceId < Interlocked.Read(ref _minValidSequenceId)
            || id.SequenceId != Interlocked.Read(ref _latestOcrSequenceId);
    }

    private bool IsSpeechJobStale(TranslationRequestIdentity id, CancellationToken ct)
    {
        return ct.IsCancellationRequested
            || State != SystemState.Translating
            || id.SessionEpoch != SessionEpoch
            || id.SettingsVersion != SettingsVersion
            || !string.Equals(id.SessionId, CurrentSessionId, StringComparison.Ordinal)
            || id.SequenceId < Interlocked.Read(ref _minValidSequenceId)
            || id.SequenceId != Interlocked.Read(ref _latestSpeechSequenceId);
    }

    private bool TryPublishSubtitle(
        TranslationRequestIdentity id,
        SubtitleItem subtitle,
        CancellationToken requestCt,
        bool isOcr,
        Action? onAcceptedBeforeNotify = null)
    {
        lock (_publishLock)
        {
            bool stale = isOcr ? IsOcrJobStale(id, requestCt) : IsSpeechJobStale(id, requestCt);
            if (stale || id.SequenceId < Interlocked.Read(ref _lastPublishedSequenceId))
            {
                Interlocked.Increment(ref _rejectedStaleCount);
                return false;
            }

            Interlocked.Exchange(ref _lastPublishedSequenceId, id.SequenceId);
            CurrentSubtitle = subtitle;
            if (isOcr)
            {
                _lastStableOcrText = subtitle.OriginalText;
                _lastOcrSeenTimeUtc = DateTime.UtcNow;
            }

            lock (_conversationContext)
            {
                _conversationContext.Enqueue(subtitle);
                while (_conversationContext.Count > 5)
                {
                    _conversationContext.Dequeue();
                }
            }

            onAcceptedBeforeNotify?.Invoke();
            SubtitleUpdated?.Invoke(this, subtitle);
            return true;
        }
    }

    private async Task OcrTranslationWorkerLoopAsync(ChannelReader<OcrTranslationJob> reader, CancellationToken sessionCt)
    {
        try
        {
            await foreach (var job in reader.ReadAllAsync(sessionCt))
            {
                LastQueueWaitMs = Math.Max(0, (long)(DateTime.UtcNow - job.Identity.CreatedAtUtc).TotalMilliseconds);

                // Drop already-superseded job before calling translation provider
                if (IsOcrJobStale(job.Identity, job.RequestCt))
                {
                    Interlocked.Increment(ref _droppedObsoleteCount);
                    OcrQueueLength = 0;
                    continue;
                }

                var activeProvider = _providerResolver.ResolveActiveProvider();
                var analysis = job.Analysis;
                var reqTargets = job.Identity.TargetLanguages;

                // Pre-fill existing subtitle lines from bilingual video or same-language source
                string zh = string.Empty;
                string en = string.Empty;
                string vi = string.Empty;
                var neededTargets = new List<TargetLanguage>();

                if (reqTargets.Contains(TargetLanguage.Chinese))
                {
                    if (analysis.DetectedPrimaryLanguage == "zh") zh = analysis.PrimarySourceText;
                    else if (analysis.ExistingSecondaryLanguage == "zh" && !string.IsNullOrWhiteSpace(analysis.ExistingSecondaryText)) zh = analysis.ExistingSecondaryText;
                    else neededTargets.Add(TargetLanguage.Chinese);
                }
                if (reqTargets.Contains(TargetLanguage.English))
                {
                    if (analysis.DetectedPrimaryLanguage == "en") en = analysis.PrimarySourceText;
                    else if (analysis.ExistingSecondaryLanguage == "en" && !string.IsNullOrWhiteSpace(analysis.ExistingSecondaryText)) en = analysis.ExistingSecondaryText;
                    else neededTargets.Add(TargetLanguage.English);
                }
                if (reqTargets.Contains(TargetLanguage.Vietnamese))
                {
                    if (analysis.DetectedPrimaryLanguage == "vi") vi = analysis.PrimarySourceText;
                    else if (analysis.ExistingSecondaryLanguage == "vi" && !string.IsNullOrWhiteSpace(analysis.ExistingSecondaryText)) vi = analysis.ExistingSecondaryText;
                    else neededTargets.Add(TargetLanguage.Vietnamese);
                }

                PipelineStatusText = "Đang dịch (Translating)...";
                StatusUpdated?.Invoke(this, PipelineStatusText);

                try
                {
                    var transSw = Stopwatch.StartNew();
                    TranslationResult transResult;

                    if (neededTargets.Count > 0)
                    {
                        var srcLangEnum = MapSourceLanguage(analysis.DetectedPrimaryLanguage);
                        transResult = await TranslateWithOptionalContextAsync(
                            activeProvider,
                            analysis.PrimarySourceText,
                            srcLangEnum,
                            neededTargets,
                            job.RequestCt);

                        if (neededTargets.Contains(TargetLanguage.Chinese) && string.IsNullOrEmpty(zh))
                            zh = SubtitleTextProcessor.CleanTranslationOutput(transResult.GetTranslation(TargetLanguage.Chinese), analysis.PrimarySourceText);
                        if (neededTargets.Contains(TargetLanguage.English) && string.IsNullOrEmpty(en))
                            en = SubtitleTextProcessor.CleanTranslationOutput(transResult.GetTranslation(TargetLanguage.English), analysis.PrimarySourceText);
                        if (neededTargets.Contains(TargetLanguage.Vietnamese) && string.IsNullOrEmpty(vi))
                            vi = SubtitleTextProcessor.CleanTranslationOutput(transResult.GetTranslation(TargetLanguage.Vietnamese), analysis.PrimarySourceText);
                    }
                    else
                    {
                        transResult = new TranslationResult
                        {
                            OriginalText = analysis.PrimarySourceText,
                            DetectedLanguage = analysis.DetectedPrimaryLanguage,
                            Translations = new Dictionary<TargetLanguage, string>
                            {
                                [TargetLanguage.Chinese] = zh,
                                [TargetLanguage.English] = en,
                                [TargetLanguage.Vietnamese] = vi
                            },
                            LatencyMs = 0,
                            Timestamp = DateTime.UtcNow,
                            ProviderName = activeProvider.ProviderName
                        };
                    }

                    transSw.Stop();

                    // Re-check before constructing/publishing subtitle
                    if (IsOcrJobStale(job.Identity, job.RequestCt))
                    {
                        Interlocked.Increment(ref _rejectedStaleCount);
                        continue;
                    }

                    var subtitle = new SubtitleItem
                    {
                        OriginalText = analysis.PrimarySourceText,
                        OriginalLanguageCode = analysis.DetectedPrimaryLanguage,
                        ChineseText = zh,
                        EnglishText = en,
                        VietnameseText = vi,
                        DisplayMode = _settingsService.Settings.DisplayMode,
                        SourceMode = TranslationMode.ScreenOcr,
                        UpdatedAt = DateTime.UtcNow,
                        SessionId = job.Identity.SessionId,
                        SequenceId = job.Identity.SequenceId,
                        SettingsVersion = job.Identity.SettingsVersion
                    };

                    if (!TryPublishSubtitle(job.Identity, subtitle, job.RequestCt, isOcr: true, onAcceptedBeforeNotify: () =>
                        {
                            LastTranslationResult = transResult;
                            TranslationStatusText = activeProvider.StatusMessage;

                            if (neededTargets.Count > 0)
                            {
                                LastTranslationLatencyMs = transResult.LatencyMs > 0 ? transResult.LatencyMs : Math.Max(1, transSw.ElapsedMilliseconds);
                                long totalEndToEnd = Math.Max(1, job.CaptureAndOcrElapsedMs + LastQueueWaitMs + transSw.ElapsedMilliseconds);
                                LastEndToEndLatencyMs = totalEndToEnd;
                                RecordLatency(totalEndToEnd);
                            }
                            else
                            {
                                LastTranslationLatencyMs = 0;
                                LastEndToEndLatencyMs = 0;
                            }
                        }))
                    {
                        continue;
                    }

                    PipelineStatusText = "Bản dịch sẵn sàng (Translation ready)";
                    StatusUpdated?.Invoke(this, PipelineStatusText);
                }
                catch (OperationCanceledException)
                {
                    // Discard cancelled request cleanly without touching CurrentSubtitle
                }
                catch (Exception ex)
                {
                    // Never allow a stale or cancelled request from an old session/settings version to overwrite UI!
                    if (IsOcrJobStale(job.Identity, job.RequestCt))
                    {
                        Interlocked.Increment(ref _rejectedStaleCount);
                        continue;
                    }

                    _lastStableOcrText = string.Empty;
                    _lastSubmittedOcrText = string.Empty;
                    LastTranslationLatencyMs = 0;
                    LastEndToEndLatencyMs = 0;
                    _latencyHistory.Clear();
                    AverageLatencyMs = 0;
                    LatencyUpdated?.Invoke(this, 0);

                    TranslationStatusText = activeProvider.StatusMessage;
                    PipelineStatusText = $"Lỗi dịch: {ex.Message}";
                    StatusUpdated?.Invoke(this, PipelineStatusText);

                    string errPlaceholder = ex.Message.Contains("API Key", StringComparison.OrdinalIgnoreCase)
                        ? "[Azure chưa cấu hình — Vui lòng nhập API Key trong tab Cài đặt hoặc chuyển sang Offline AI]"
                        : ex.Message.Contains("MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase)
                            ? "[MODEL NOT INSTALLED — Chưa cài mô hình dịch Offline]"
                            : $"[Lỗi dịch: {ex.Message}]";

                    var fallbackSubtitle = new SubtitleItem
                    {
                        OriginalText = analysis.PrimarySourceText,
                        OriginalLanguageCode = analysis.DetectedPrimaryLanguage,
                        ChineseText = !string.IsNullOrEmpty(zh) ? zh : (neededTargets.Contains(TargetLanguage.Chinese) ? errPlaceholder : string.Empty),
                        EnglishText = !string.IsNullOrEmpty(en) ? en : (neededTargets.Contains(TargetLanguage.English) ? errPlaceholder : string.Empty),
                        VietnameseText = !string.IsNullOrEmpty(vi) ? vi : (neededTargets.Contains(TargetLanguage.Vietnamese) ? errPlaceholder : string.Empty),
                        DisplayMode = _settingsService.Settings.DisplayMode,
                        SourceMode = TranslationMode.ScreenOcr,
                        UpdatedAt = DateTime.UtcNow,
                        SessionId = job.Identity.SessionId,
                        SequenceId = job.Identity.SequenceId,
                        SettingsVersion = job.Identity.SettingsVersion
                    };
                    TryPublishSubtitle(job.Identity, fallbackSubtitle, job.RequestCt, isOcr: true);
                }
                finally
                {
                    OcrQueueLength = 0;
                    lock (_translationCtsLock)
                    {
                        if (ReferenceEquals(_inFlightTranslationCts, job.JobCts))
                        {
                            _inFlightTranslationCts = null;
                        }
                    }
                    try { job.JobCts.Dispose(); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }
    }

    private async Task SpeechWorkerLoopAsync(ChannelReader<SpeechSegment> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var segment in reader.ReadAllAsync(ct))
            {
                if (State != SystemState.Running || Mode == TranslationMode.ScreenOcr)
                {
                    continue;
                }

                if (_speechService == null)
                {
                    continue;
                }

                // Avoid burning CPU on stale queued speech after stalls or pauses
                double queueAgeMs = (DateTime.UtcNow - segment.CreatedAtUtc).TotalMilliseconds;
                if (queueAgeMs > MaxSpeechSegmentAgeMs)
                {
                    Interlocked.Increment(ref _droppedObsoleteCount);
                    continue;
                }
                LastSpeechQueueWaitMs = Math.Max(0, (long)queueAgeMs);

                try
                {
                    var sw = Stopwatch.StartNew();
                    string targetLang = ResolveSpeechLanguage(_settingsService.Settings);

                    PipelineStatusText = "Đang nhận diện giọng nói (Whisper)...";
                    StatusUpdated?.Invoke(this, PipelineStatusText);

                    var speechResult = await _speechService.RecognizeAsync(segment.PcmData, targetLang, ct);
                    sw.Stop();

                    if (ct.IsCancellationRequested || State != SystemState.Running || Mode == TranslationMode.ScreenOcr)
                    {
                        continue;
                    }

                    LastSpeechLatencyMs = speechResult.LatencyMs;
                    LastRecognizedSpeechText = speechResult.Text;
                    LastSpeechLanguage = speechResult.DetectedLanguage;
                    SpeechRecognized?.Invoke(this, speechResult);

                    if (!speechResult.HasText || string.IsNullOrWhiteSpace(speechResult.Text))
                    {
                        continue;
                    }

                    // Check duplicate against last stable speech (including CJK script & punctuation normalization)
                    string normalizedSpeech = speechResult.Text.Trim();
                    string speechCanon = NormalizeForDuplicateComparison(normalizedSpeech);
                    string lastSpeechCanon = NormalizeForDuplicateComparison(_lastStableSpeechText);
                    if (!string.IsNullOrEmpty(speechCanon) &&
                        string.Equals(speechCanon, lastSpeechCanon, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref _duplicateSuppressedCount);
                        continue;
                    }

                    // In Hybrid mode: only suppress speech when an active (non-stale) OCR subtitle is currently present on screen and semantically equivalent
                    if (Mode == TranslationMode.Hybrid && _isOcrCurrentlyPresentOnScreen && !string.IsNullOrWhiteSpace(_lastStableOcrText))
                    {
                        if (SubtitleTextProcessor.ShouldSuppressHybridSpeechDuplicate(
                            _lastStableOcrText,
                            normalizedSpeech,
                            _lastOcrSeenTimeUtc,
                            DateTime.UtcNow,
                            LocalChineseConverter.ToSimplified))
                        {
                            Interlocked.Increment(ref _duplicateSuppressedCount);
                            continue;
                        }
                    }

                    long seqId = Interlocked.Increment(ref _currentSequenceId);
                    Interlocked.Exchange(ref _latestSpeechSequenceId, seqId);
                    var identity = new TranslationRequestIdentity(
                        SessionId: CurrentSessionId,
                        SessionEpoch: SessionEpoch,
                        SequenceId: seqId,
                        SettingsVersion: SettingsVersion,
                        InputSource: TranslationMode.AudioRealtime,
                        SourceRegion: _selectedRegion,
                        SourceLanguage: speechResult.DetectedLanguage,
                        TargetLanguages: _settingsService.Settings.TargetLanguages.ToArray(),
                        CreatedAtUtc: DateTime.UtcNow);

                    _lastStableSpeechText = normalizedSpeech;
                    _lastTextSeenTime = DateTime.UtcNow;

                    var activeProvider = _providerResolver.ResolveActiveProvider();
                    var transSw = Stopwatch.StartNew();

                    var transResult = await TranslateWithOptionalContextAsync(
                        activeProvider,
                        normalizedSpeech,
                        MapSourceLanguage(speechResult.DetectedLanguage),
                        identity.TargetLanguages,
                        ct);

                    transSw.Stop();

                    if (IsSpeechJobStale(identity, ct))
                    {
                        Interlocked.Increment(ref _rejectedStaleCount);
                        continue;
                    }

                    var subtitle = AssembleSubtitle(normalizedSpeech, transResult, TranslationMode.AudioRealtime, identity);
                    if (!TryPublishSubtitle(identity, subtitle, ct, isOcr: false, onAcceptedBeforeNotify: () =>
                        {
                            LastTranslationLatencyMs = transSw.ElapsedMilliseconds;
                            LastTranslationResult = transResult;
                            TranslationStatusText = activeProvider.StatusMessage;

                            long totalLatency = sw.ElapsedMilliseconds + transSw.ElapsedMilliseconds;
                            LastEndToEndLatencyMs = totalLatency;
                            RecordLatency(totalLatency);
                        }))
                    {
                        continue;
                    }

                    PipelineStatusText = "Bản dịch âm thanh sẵn sàng (Audio translated)";
                    StatusUpdated?.Invoke(this, PipelineStatusText);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PipelineStatusText = $"Lỗi xử lý âm thanh: {ex.Message}";
                    StatusUpdated?.Invoke(this, PipelineStatusText);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean shutdown
        }
    }

    private void OnBlankFrameDetected(bool frameChanged = false)
    {
        _consecutiveBlankFrames++;
        // If the screen visually changed (`frameChanged == true`) and OCR returned no text, or if we have seen >= 2 consecutive blank OCRs,
        // the subtitle has visually disappeared from the screen — immediately mark OCR as absent so delayed speech in Hybrid mode is not suppressed.
        if (frameChanged || _consecutiveBlankFrames >= 2 || _unchangedEmptyOrFailureRetries >= MaxUnchangedEmptyOrFailedOcrRetries)
        {
            _isOcrCurrentlyPresentOnScreen = false;
            _lastOcrSeenTimeUtc = DateTime.MinValue;
            _lastStableOcrText = string.Empty;
        }

        // Do not prematurely hide/clear subtitles on temporary dropped frames or brief pauses between dialogue!
        // Require at least 16 consecutive blank frames AND at least 6.5s since text was last seen so brief OCR misses or short dialogue gaps don't cause hide/show flicker.
        if (CurrentSubtitle.HasContent && _consecutiveBlankFrames >= 16 && _lastTextSeenTime != DateTime.MinValue)
        {
            int timeoutMs = CurrentSubtitle.SourceMode == TranslationMode.AudioRealtime ? 7500 : 6500;
            if ((DateTime.UtcNow - _lastTextSeenTime).TotalMilliseconds > timeoutMs)
            {
                _lastStableOcrText = string.Empty;
                _lastSubmittedOcrText = string.Empty;
                _lastStableSpeechText = string.Empty;
                _lastTextSeenTime = DateTime.MinValue;
                _lastOcrSeenTimeUtc = DateTime.MinValue;
                _isOcrCurrentlyPresentOnScreen = false;
                _consecutiveBlankFrames = 0;
                ClearSubtitle();
            }
        }
    }

    private void CheckSubtitleTimeout(int blankDebounceMs)
    {
        OnBlankFrameDetected();
    }

    private SubtitleItem AssembleSubtitle(
        string originalText,
        TranslationResult trans,
        TranslationMode sourceMode = TranslationMode.ScreenOcr,
        TranslationRequestIdentity? identity = null)
    {
        string en = SubtitleTextProcessor.CleanTranslationOutput(trans.GetTranslation(TargetLanguage.English), originalText);
        string vi = SubtitleTextProcessor.CleanTranslationOutput(trans.GetTranslation(TargetLanguage.Vietnamese), originalText);
        string zh = SubtitleTextProcessor.CleanTranslationOutput(trans.GetTranslation(TargetLanguage.Chinese), originalText);

        string detectedLang = !string.IsNullOrWhiteSpace(trans.DetectedLanguage) && trans.DetectedLanguage != "auto"
            ? trans.DetectedLanguage
            : SubtitleTextProcessor.DetectLanguage(originalText);

        if (detectedLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            zh = originalText;
        }
        else if (detectedLang.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            en = originalText;
        }
        else if (detectedLang.Equals("vi", StringComparison.OrdinalIgnoreCase))
        {
            vi = originalText;
        }

        return new SubtitleItem
        {
            OriginalText = originalText,
            OriginalLanguageCode = detectedLang,
            ChineseText = zh,
            EnglishText = en,
            VietnameseText = vi,
            DisplayMode = _settingsService.Settings.DisplayMode,
            SourceMode = sourceMode,
            UpdatedAt = DateTime.UtcNow,
            SessionId = identity?.SessionId ?? CurrentSessionId,
            SequenceId = identity?.SequenceId ?? 0,
            SettingsVersion = identity?.SettingsVersion ?? SettingsVersion
        };
    }

    private Task<TranslationResult> TranslateWithOptionalContextAsync(
        ITranslationProvider provider,
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct)
    {
        if (_settingsService.Settings.EnableContextAwareTranslation &&
            provider is IContextAwareTranslationProvider contextProvider)
        {
            return contextProvider.TranslateWithContextAsync(text, source, targets, RecentConversationContext, ct);
        }

        return provider.TranslateAsync(text, source, targets, ct);
    }

    private static string NormalizeForDuplicateComparison(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string simp = LocalChineseConverter.ToSimplified(text.Trim());
        var sb = new System.Text.StringBuilder(simp.Length);
        bool prevWasSpace = false;
        bool prevWasCjk = false;

        foreach (char c in simp)
        {
            if (char.IsPunctuation(c) || c is '。' or '！' or '？' or '，' or '；' or '：' or '、' or '“' or '”' or '‘' or '’')
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!prevWasSpace && !prevWasCjk && sb.Length > 0)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
                continue;
            }

            bool isCjk = c >= 0x4E00 && c <= 0x9FFF;
            if (isCjk && prevWasSpace && sb.Length > 0 && sb[^1] == ' ')
            {
                sb.Length--;
            }

            sb.Append(char.ToLowerInvariant(c));
            prevWasSpace = false;
            prevWasCjk = isCjk;
        }

        return sb.ToString().Trim();
    }

    private static string ResolveSpeechLanguage(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SpeechLanguage) &&
            !settings.SpeechLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return settings.SpeechLanguage;
        }

        return settings.SourceLanguage switch
        {
            SourceLanguage.English => "en",
            SourceLanguage.Chinese => "zh",
            SourceLanguage.Vietnamese => "vi",
            _ => "auto"
        };
    }

    private static SourceLanguage MapSourceLanguage(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return SourceLanguage.Auto;
        lang = lang.ToLowerInvariant();
        if (lang.StartsWith("zh")) return SourceLanguage.Chinese;
        if (lang.StartsWith("en")) return SourceLanguage.English;
        if (lang.StartsWith("vi")) return SourceLanguage.Vietnamese;
        return SourceLanguage.Auto;
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
        if (_ocrTranslationTask != null)
        {
            try { await _ocrTranslationTask; } catch { }
        }
        if (_speechWorkerTask != null)
        {
            try { await _speechWorkerTask; } catch { }
        }
    }
}
