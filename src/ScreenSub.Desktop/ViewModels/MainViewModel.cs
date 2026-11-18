using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Overlay;

namespace ScreenSub.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IScreenCaptureService _captureService;
    private readonly ISubtitleCoordinator _coordinator;
    private readonly ISettingsService _settingsService;
    private readonly SubtitleOverlayWindow _overlayWindow;
    private readonly Func<Views.RegionSelectionWindow> _regionSelectorFactory;

    // Sidebar Navigation
    [ObservableProperty]
    private string _currentTab = "Dashboard";

    // System Status
    [ObservableProperty]
    private SystemState _state = SystemState.Idle;

    [ObservableProperty]
    private string _ocrStatusText = "Sẵn sàng";

    [ObservableProperty]
    private string _translationStatusText = "Chưa kết nối";

    [ObservableProperty]
    private string _pipelineStatusText = "Sẵn sàng";

    [ObservableProperty]
    private long _averageLatencyMs = 0;

    [ObservableProperty]
    private string _formattedLatencyText = "N/A";

    [ObservableProperty]
    private bool _isTranslationHealthy = true;

    [ObservableProperty]
    private string _ocrFooterText = "OCR: RapidOCR ONNX";

    [ObservableProperty]
    private string _ocrFooterStatusIcon = "✓";

    [ObservableProperty]
    private string _ocrFooterColor = "#10B981";

    [ObservableProperty]
    private string _translationFooterText = "Dịch: M2M-100 Offline";

    [ObservableProperty]
    private string _translationFooterStatusIcon = "✓";

    [ObservableProperty]
    private string _translationFooterColor = "#10B981";

    [ObservableProperty]
    private string _diagnosticsTooltipText = "Chưa có dữ liệu đo hiệu năng";

    [ObservableProperty]
    private string _captureFpsText = "0 FPS";

    [ObservableProperty]
    private string _ocrLatencyText = "N/A";

    [ObservableProperty]
    private string _translationLatencyText = "N/A";

    [ObservableProperty]
    private string _endToEndLatencyText = "N/A";

    [ObservableProperty]
    private string _cacheHitRateText = "0%";

    [ObservableProperty]
    private string _ocrQueueLengthText = "0";

    [ObservableProperty]
    private string _formattedRegionSize = "Chưa chọn vùng";

    // Mode Selection (Phase 8)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScreenOcrMode))]
    [NotifyPropertyChangedFor(nameof(IsAudioRealtimeMode))]
    [NotifyPropertyChangedFor(nameof(IsHybridMode))]
    [NotifyPropertyChangedFor(nameof(ShowScreenCaptureControls))]
    [NotifyPropertyChangedFor(nameof(ShowAudioControls))]
    private TranslationMode _currentMode = TranslationMode.ScreenOcr;

    public bool IsScreenOcrMode => CurrentMode == TranslationMode.ScreenOcr;
    public bool IsAudioRealtimeMode => CurrentMode == TranslationMode.AudioRealtime;
    public bool IsHybridMode => CurrentMode == TranslationMode.Hybrid;
    public bool ShowScreenCaptureControls => CurrentMode != TranslationMode.AudioRealtime;
    public bool ShowAudioControls => CurrentMode != TranslationMode.ScreenOcr;

    // Audio Capture & Meter (Phase 8)
    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _audioDevices = [];

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private float _audioPeakLevel = 0f;

    [ObservableProperty]
    private float _audioRmsLevel = 0f;

    [ObservableProperty]
    private string _formattedAudioLevel = "0%";

    // Speech Recognition State (Phase 8)
    [ObservableProperty]
    private string _speechStatusText = "Sẵn sàng";

    [ObservableProperty]
    private string _lastRecognizedSpeechText = string.Empty;

    [ObservableProperty]
    private string _lastSpeechLanguage = "auto";

    [ObservableProperty]
    private long _speechLatencyMs = 0;

    [ObservableProperty]
    private string _speechLanguage = "auto";

    // Capture & Region Controls
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureSourceIndex))]
    private CaptureSourceType _captureSource = CaptureSourceType.Window;

    public int CaptureSourceIndex
    {
        get => CaptureSource == CaptureSourceType.Window ? 0 : 1;
        set => CaptureSource = value == 0 ? CaptureSourceType.Window : CaptureSourceType.FullScreen;
    }

    [ObservableProperty]
    private ObservableCollection<WindowInfo> _openWindows = [];

    [ObservableProperty]
    private WindowInfo? _selectedWindow;

    [ObservableProperty]
    private bool _isFixedRegionMode = true;

    [ObservableProperty]
    private bool _isFollowWindowMode = false;

    // Languages
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceLanguageIndex))]
    private SourceLanguage _sourceLanguage = SourceLanguage.Auto;

    public int SourceLanguageIndex
    {
        get => SourceLanguage switch
        {
            SourceLanguage.Chinese => 1,
            SourceLanguage.English => 2,
            SourceLanguage.Vietnamese => 3,
            _ => 0
        };
        set => SourceLanguage = value switch
        {
            1 => SourceLanguage.Chinese,
            2 => SourceLanguage.English,
            3 => SourceLanguage.Vietnamese,
            _ => SourceLanguage.Auto
        };
    }

    [ObservableProperty]
    private bool _targetEnglish = true;

    [ObservableProperty]
    private bool _targetChinese = true;

    [ObservableProperty]
    private bool _targetVietnamese = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayModeIndex))]
    private DisplayMode _displayMode = DisplayMode.Triple;

    public int DisplayModeIndex
    {
        get => DisplayMode switch
        {
            DisplayMode.Single => 0,
            DisplayMode.Dual => 1,
            _ => 2
        };
        set => DisplayMode = value switch
        {
            0 => DisplayMode.Single,
            1 => DisplayMode.Dual,
            _ => DisplayMode.Triple
        };
    }

    // Live Preview
    [ObservableProperty]
    private BitmapSource? _previewImage;

    [ObservableProperty]
    private bool _hasPreviewImage = false;

    // Realtime Subtitles
    [ObservableProperty]
    private string _originalText = "Chưa nhận diện được văn bản";

    [ObservableProperty]
    private string _originalLanguageBadge = "Gốc (Tự động)";

    [ObservableProperty]
    private string _englishText = "Chưa có bản dịch";

    [ObservableProperty]
    private string _chineseText = "Chưa có bản dịch";

    [ObservableProperty]
    private string _vietnameseText = "Chưa có bản dịch";

    [ObservableProperty]
    private string _detectedLanguageCode = "auto";

    [ObservableProperty]
    private bool _showEnglishTranslationRow = true;

    [ObservableProperty]
    private bool _showChineseTranslationRow = true;

    [ObservableProperty]
    private bool _showVietnameseTranslationRow = true;

    [ObservableProperty]
    private bool _hasDetectedText = false;

    [ObservableProperty]
    private string _lastUpdatedText = "—";

    [ObservableProperty]
    private bool _isMockMode = false;

    // Hotkey Settings
    [ObservableProperty]
    private string _hotkeyDisplayText = "Ctrl + Alt + Q";

    [ObservableProperty]
    private bool _isRecordingHotkey = false;

    [ObservableProperty]
    private string _hotkeyStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _isHotkeyError = false;

    public event Func<ModifierKeys, Key, bool>? RequestRegisterHotkey;

    public ModifierKeys CurrentHotkeyModifiers => (ModifierKeys)_settingsService.Settings.HotkeyModifiers;
    public Key CurrentHotkeyKey => (Key)_settingsService.Settings.HotkeyKey;

    // Overlay Settings
    [ObservableProperty]
    private bool _overlayVisible = true;

    [ObservableProperty]
    private double _overlayFontSize = 28.0;

    [ObservableProperty]
    private string _overlayTextColor = "#FFFFFF";

    [ObservableProperty]
    private double _overlayBackgroundOpacity = 0.70;

    [ObservableProperty]
    private bool _overlayLocked = false;

    [ObservableProperty]
    private bool _overlayClickThrough = true;

    [ObservableProperty]
    private bool _overlayAlwaysOnTop = true;

    [ObservableProperty]
    private bool _isOverlayEditMode = false;

    [ObservableProperty]
    private double _overlayWidth = 1200;

    // First Run Experience
    [ObservableProperty]
    private bool _showFirstRunBanner = false;

    // Settings Tab Values
    private bool _isLoadingSettings = false;

    [ObservableProperty]
    private string _azureApiKey = string.Empty;

    [ObservableProperty]
    private string _azureRegion = "eastasia";

    [ObservableProperty]
    private string _azureEndpoint = "https://api.cognitive.microsofttranslator.com";

    [ObservableProperty]
    private string _geminiApiKey = string.Empty;

    [ObservableProperty]
    private string _geminiModel = "gemini-2.5-flash";

    [ObservableProperty]
    private OnlineTranslationProviderType _onlineProvider = OnlineTranslationProviderType.Azure;

    [ObservableProperty]
    private int _onlineProviderIndex = 0;

    [ObservableProperty]
    private bool _enableContextAwareTranslation = true;

    [ObservableProperty]
    private bool _useMockTranslation = false;

    [ObservableProperty]
    private string _settingsSaveMessage = string.Empty;

    [ObservableProperty]
    private string _connectionTestMessage = string.Empty;

    [ObservableProperty]
    private bool _isConnectionTestSuccess = false;

    [ObservableProperty]
    private bool _isTestingConnection = false;

    // Phase 9: Offline AI, GPU Acceleration & Performance Profiles
    private readonly IHardwareDetector? _hardwareDetector;
    private readonly IModelManager? _modelManager;

    [ObservableProperty]
    private TranslationEngineMode _translationEngineMode = TranslationEngineMode.Offline;

    [ObservableProperty]
    private AutoFallbackPolicy _autoFallbackPolicy = AutoFallbackPolicy.PreferOffline;

    [ObservableProperty]
    private bool _allowCloudFallbackOnOfflineFailure = false;

    [ObservableProperty]
    private string _activeProviderBadgeText = "OFFLINE AI — LOCAL PROCESSING";

    [ObservableProperty]
    private OverallPerformanceProfile _performanceProfile = OverallPerformanceProfile.Balanced;

    [ObservableProperty]
    private InferenceBackendType _selectedBackend = InferenceBackendType.Auto;

    // Quick Translate & Background Operation (Phase 11)
    [ObservableProperty]
    private bool _quickTranslateMode = true;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose = true;

    [ObservableProperty]
    private bool _autoPositionOverlayNearRegion = true;

    // Hardware Diagnostics
    [ObservableProperty]
    private string _cpuInfoText = "Đang kiểm tra CPU...";

    [ObservableProperty]
    private string _gpuInfoText = "Đang kiểm tra GPU...";

    [ObservableProperty]
    private string _ramInfoText = "— GB";

    [ObservableProperty]
    private string _recommendedBackendText = "DirectML";

    [ObservableProperty]
    private string _freeDiskSpaceText = "— GB khả dụng";

    // Model Manager
    public ObservableCollection<ModelMetadata> Models { get; } = new();

    [ObservableProperty]
    private ModelMetadata? _selectedModel;

    [ObservableProperty]
    private string _modelStorageDirectory = string.Empty;

    [ObservableProperty]
    private string _modelActionMessage = string.Empty;

    [ObservableProperty]
    private bool _isModelBusy = false;

    public MainViewModel(
        IScreenCaptureService captureService,
        ISubtitleCoordinator coordinator,
        ISettingsService settingsService,
        SubtitleOverlayWindow overlayWindow,
        Func<Views.RegionSelectionWindow> regionSelectorFactory,
        IHardwareDetector? hardwareDetector = null,
        IModelManager? modelManager = null)
    {
        _captureService = captureService;
        _coordinator = coordinator;
        _settingsService = settingsService;
        _overlayWindow = overlayWindow;
        _regionSelectorFactory = regionSelectorFactory;
        _hardwareDetector = hardwareDetector;
        _modelManager = modelManager;

        LoadSettingsIntoProperties();
        RefreshWindows();
        LoadHardwareInfo();
        RefreshModelList();

        // Wire coordinator events
        _coordinator.StateChanged += (_, s) =>
        {
            RunOnUiThread(() =>
            {
                State = s;
                OcrStatusText = _coordinator.OcrStatusText;
                TranslationStatusText = _coordinator.TranslationStatusText;
                PipelineStatusText = _coordinator.PipelineStatusText;
                ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
                UpdateTranslationHealthState();
                UpdateDiagnostics();
            });
        };

        _coordinator.StatusUpdated += (_, _) =>
        {
            RunOnUiThread(() =>
            {
                OcrStatusText = _coordinator.OcrStatusText;
                TranslationStatusText = _coordinator.TranslationStatusText;
                PipelineStatusText = _coordinator.PipelineStatusText;
                ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
                UpdateTranslationHealthState();
                UpdateDiagnostics();
            });
        };

        _coordinator.LatencyUpdated += (_, lat) =>
        {
            RunOnUiThread(() =>
            {
                AverageLatencyMs = lat;
                FormattedLatencyText = lat > 0 ? $"≈ {lat} ms" : "N/A";
                UpdateDiagnostics();
            });
        };

        _coordinator.FrameCaptured += (_, frameBytes) =>
        {
            RunOnUiThread(() =>
            {
                if (frameBytes == null || frameBytes.Length == 0)
                {
                    PreviewImage = null;
                    HasPreviewImage = false;
                }
                else
                {
                    PreviewImage = LoadBitmapFromBytes(frameBytes);
                    HasPreviewImage = PreviewImage != null;
                }
                UpdateDiagnostics();
            });
        };

        _coordinator.OcrTextRecognized += (_, ocrInfo) =>
        {
            RunOnUiThread(() =>
            {
                if (!string.IsNullOrWhiteSpace(ocrInfo.Text))
                {
                    OriginalText = ocrInfo.Text;
                    DetectedLanguageCode = string.IsNullOrWhiteSpace(ocrInfo.LanguageCode) ? "auto" : ocrInfo.LanguageCode;
                    HasDetectedText = true;
                    LastUpdatedText = DateTime.Now.ToString("HH:mm:ss");
                    OriginalLanguageBadge = DetectedLanguageCode switch
                    {
                        "zh" => "Gốc (Trung)",
                        "en" => "Gốc (Anh)",
                        "vi" => "Gốc (Việt)",
                        "auto" or "" => "Gốc (Tự động)",
                        _ => $"Gốc ({DetectedLanguageCode.ToUpperInvariant()})"
                    };
                    UpdateTranslationRowVisibility();
                    UpdateDiagnostics();
                }
            });
        };

        _coordinator.SubtitleUpdated += (_, item) =>
        {
            RunOnUiThread(() =>
            {
                // Final UI-thread stale-result validation: never let queued/stale subtitle callbacks
                // overwrite newer content or re-appear after Stop/Reset/Settings change.
                if (item.HasContent && item.SequenceId > 0)
                {
                    if (!string.Equals(item.SessionId, _coordinator.CurrentSessionId, StringComparison.Ordinal) ||
                        item.SettingsVersion < _coordinator.SettingsVersion ||
                        _coordinator.State != SystemState.Translating)
                    {
                        return;
                    }
                }

                if (!item.HasContent)
                {
                    OriginalText = "Chưa nhận diện được văn bản";
                    EnglishText = "Chưa có bản dịch";
                    ChineseText = "Chưa có bản dịch";
                    VietnameseText = "Chưa có bản dịch";
                    OriginalLanguageBadge = "Gốc (Tự động)";
                    DetectedLanguageCode = "auto";
                    HasDetectedText = false;
                    UpdateTranslationRowVisibility();
                }
                else
                {
                    OriginalText = string.IsNullOrWhiteSpace(item.OriginalText) ? "—" : item.OriginalText;
                    EnglishText = string.IsNullOrWhiteSpace(item.EnglishText) ? "—" : item.EnglishText;
                    ChineseText = string.IsNullOrWhiteSpace(item.ChineseText) ? "—" : item.ChineseText;
                    VietnameseText = string.IsNullOrWhiteSpace(item.VietnameseText) ? "—" : item.VietnameseText;
                    HasDetectedText = true;
                    DetectedLanguageCode = item.OriginalLanguageCode;
                    LastUpdatedText = item.UpdatedAt.ToLocalTime().ToString("HH:mm:ss");

                    OriginalLanguageBadge = item.OriginalLanguageCode switch
                    {
                        "zh" => "Gốc (Trung)",
                        "en" => "Gốc (Anh)",
                        "vi" => "Gốc (Việt)",
                        "auto" or "" or null => "Gốc (Tự động)",
                        _ => $"Gốc ({item.OriginalLanguageCode.ToUpperInvariant()})"
                    };

                    UpdateTranslationRowVisibility();
                }

                var ovSw = System.Diagnostics.Stopwatch.StartNew();
                if (OverlayVisible && item.HasContent)
                {
                    _overlayWindow.EnsureShown();
                }
                _overlayWindow.UpdateSubtitle(item);
                ovSw.Stop();
                _coordinator.LastOverlayUpdateLatencyMs = ovSw.ElapsedMilliseconds;
                UpdateDiagnostics();
            });
        };

        // Wire Audio & Speech Events (Phase 8)
        _coordinator.AudioLevelChanged += (_, e) =>
        {
            RunOnUiThread(() =>
            {
                AudioPeakLevel = e.PeakLevel;
                AudioRmsLevel = e.RmsLevel;
                FormattedAudioLevel = $"{(int)(e.PeakLevel * 100)}%";
            });
        };

        _coordinator.SpeechRecognized += (_, res) =>
        {
            RunOnUiThread(() =>
            {
                LastRecognizedSpeechText = res.Text;
                LastSpeechLanguage = res.DetectedLanguage;
                SpeechLatencyMs = res.LatencyMs;
                SpeechStatusText = $"Đã nhận diện ({res.DetectedLanguage})";
            });
        };

        // Initialize overlay
        _overlayWindow.UpdateStyling(OverlayFontSize, OverlayTextColor, OverlayBackgroundOpacity);
        _overlayWindow.UpdateDisplayMode(DisplayMode);
        _overlayWindow.SetLocked(OverlayLocked);
        _overlayWindow.SetClickThrough(OverlayClickThrough);
        _overlayWindow.Topmost = OverlayAlwaysOnTop;
        _overlayWindow.SetOverlayVisible(OverlayVisible);
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private void UpdateTranslationHealthState()
    {
        bool unconfiguredAzure = ActiveProviderBadgeText.Contains("CHƯA CẤU HÌNH", StringComparison.OrdinalIgnoreCase)
            || TranslationStatusText.Contains("Chưa cấu hình", StringComparison.OrdinalIgnoreCase);
        bool missingModel = ActiveProviderBadgeText.Contains("MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase)
            || TranslationStatusText.Contains("MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase);
        bool hasError = TranslationStatusText.Contains("Lỗi", StringComparison.OrdinalIgnoreCase);

        IsTranslationHealthy = !unconfiguredAzure && !missingModel && !hasError;

        // Dynamic footer binding for OCR
        if (OcrStatusText.Contains("Sẵn sàng", StringComparison.OrdinalIgnoreCase))
        {
            OcrFooterText = OcrStatusText.Contains("RapidOCR", StringComparison.OrdinalIgnoreCase) ? "OCR: RapidOCR ONNX" : "OCR: PaddleOCR";
            OcrFooterStatusIcon = "✓";
            OcrFooterColor = "#10B981"; // Green
        }
        else if (OcrStatusText.Contains("Chưa cài", StringComparison.OrdinalIgnoreCase) || OcrStatusText.Contains("Lỗi", StringComparison.OrdinalIgnoreCase))
        {
            OcrFooterText = "OCR: Chưa sẵn sàng";
            OcrFooterStatusIcon = "✕";
            OcrFooterColor = "#EF4444"; // Red
        }
        else
        {
            OcrFooterText = $"OCR: {OcrStatusText}";
            OcrFooterStatusIcon = "✓";
            OcrFooterColor = "#10B981";
        }

        // Dynamic footer binding for Translation
        if (IsMockMode)
        {
            TranslationFooterText = "Dịch: Mock (Demo)";
            TranslationFooterStatusIcon = "✓";
            TranslationFooterColor = "#F59E0B"; // Amber
        }
        else if (ActiveProviderBadgeText.Contains("OFFLINE AI", StringComparison.OrdinalIgnoreCase) || TranslationEngineMode == TranslationEngineMode.Offline)
        {
            if (missingModel)
            {
                TranslationFooterText = "Dịch: Thiếu Model";
                TranslationFooterStatusIcon = "✕";
                TranslationFooterColor = "#EF4444"; // Red
            }
            else
            {
                TranslationFooterText = "Dịch: M2M-100 Offline";
                TranslationFooterStatusIcon = "✓";
                TranslationFooterColor = "#10B981"; // Green
            }
        }
        else if (TranslationEngineMode == TranslationEngineMode.Online)
        {
            bool isGemini = ActiveProviderBadgeText.Contains("GEMINI", StringComparison.OrdinalIgnoreCase);
            if (unconfiguredAzure)
            {
                TranslationFooterText = isGemini ? "Dịch: Gemini Chưa Cấu Hình" : "Dịch: Azure Chưa Cấu Hình";
                TranslationFooterStatusIcon = "⚠";
                TranslationFooterColor = "#F59E0B"; // Amber
            }
            else
            {
                TranslationFooterText = isGemini ? "Dịch: Gemini AI (Ngữ cảnh)" : "Dịch: Azure Online";
                TranslationFooterStatusIcon = "✓";
                TranslationFooterColor = "#10B981"; // Green
            }
        }
        else // Auto
        {
            if (missingModel && unconfiguredAzure)
            {
                TranslationFooterText = "Dịch: Chưa Cấu Hình";
                TranslationFooterStatusIcon = "✕";
                TranslationFooterColor = "#EF4444";
            }
            else if (!missingModel)
            {
                TranslationFooterText = "Dịch: Auto (Offline AI)";
                TranslationFooterStatusIcon = "✓";
                TranslationFooterColor = "#10B981";
            }
            else
            {
                TranslationFooterText = "Dịch: Auto";
                TranslationFooterStatusIcon = "✓";
                TranslationFooterColor = "#10B981";
            }
        }
    }

    private void LoadSettingsIntoProperties()
    {
        _isLoadingSettings = true;
        try
        {
            var s = _settingsService.Settings;
            CurrentMode = s.TranslationMode;
        SpeechLanguage = s.SpeechLanguage;
        CaptureSource = s.CaptureSource;

        RefreshAudioDevices();
        if (!string.IsNullOrWhiteSpace(s.SelectedAudioDeviceId))
        {
            SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == s.SelectedAudioDeviceId) ?? SelectedAudioDevice;
        }

        SourceLanguage = s.SourceLanguage;
        if (s.TargetLanguages != null && s.TargetLanguages.Count > 0)
        {
            TargetEnglish = s.TargetLanguages.Contains(TargetLanguage.English);
            TargetChinese = s.TargetLanguages.Contains(TargetLanguage.Chinese);
            TargetVietnamese = s.TargetLanguages.Contains(TargetLanguage.Vietnamese);
        }
        DisplayMode = s.DisplayMode;
        OverlayFontSize = s.OverlayFontSize;
        OverlayTextColor = s.OverlayTextColor;
        OverlayBackgroundOpacity = s.OverlayBackgroundOpacity;
        OverlayLocked = s.OverlayLocked;
        OverlayClickThrough = s.OverlayClickThrough;
        OverlayAlwaysOnTop = s.OverlayAlwaysOnTop;
        OverlayVisible = s.OverlayVisible;

        FormattedRegionSize = s.LastRegion.IsEmpty ? "Chưa chọn vùng" : s.LastRegion.FormattedSize;

        HotkeyDisplayText = string.IsNullOrWhiteSpace(s.HotkeyDisplayText) ? "Ctrl + Alt + Q" : s.HotkeyDisplayText;

        AzureApiKey = s.AzureApiKey;
        AzureRegion = s.AzureRegion;
        AzureEndpoint = string.IsNullOrWhiteSpace(s.AzureEndpoint)
            ? "https://api.cognitive.microsofttranslator.com"
            : s.AzureEndpoint;
        GeminiApiKey = s.GeminiApiKey;
        GeminiModel = string.IsNullOrWhiteSpace(s.GeminiModel)
            ? "gemini-2.5-flash"
            : s.GeminiModel;
        OnlineProvider = s.OnlineProvider;
        OnlineProviderIndex = s.OnlineProvider == OnlineTranslationProviderType.Gemini ? 1 : 0;
        EnableContextAwareTranslation = s.EnableContextAwareTranslation;
        UseMockTranslation = s.UseMockTranslation;
        IsMockMode = s.UseMockTranslation;

        IsFixedRegionMode = s.RegionMode == RegionMode.FixedRegion;
        IsFollowWindowMode = s.RegionMode == RegionMode.FollowWindow;

        OcrStatusText = _coordinator.OcrStatusText;
        TranslationStatusText = _coordinator.TranslationStatusText;
        SpeechStatusText = _coordinator.SpeechStatusText;

        TranslationEngineMode = s.TranslationEngineMode;
        AutoFallbackPolicy = s.AutoFallbackPolicy;
        AllowCloudFallbackOnOfflineFailure = s.AllowCloudFallbackOnOfflineFailure;
        PerformanceProfile = s.PerformanceProfile;
        SelectedBackend = s.SelectedBackend;
        ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
        UpdateTranslationHealthState();
        UpdateTranslationRowVisibility();

        QuickTranslateMode = s.QuickTranslateMode;
        MinimizeToTrayOnClose = s.MinimizeToTrayOnClose;
        AutoPositionOverlayNearRegion = s.AutoPositionOverlayNearRegion;

        OverlayWidth = s.OverlayWidth > 0 ? s.OverlayWidth : 1200;
        ShowFirstRunBanner = !s.HasCompletedFirstRunGuide;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    [RelayCommand]
    public void Navigate(string tab)
    {
        if (string.Equals(tab, "Cài đặt", StringComparison.OrdinalIgnoreCase))
        {
            CurrentTab = "Settings";
            return;
        }
        CurrentTab = tab;
    }

    [RelayCommand]
    public void StartTranslation()
    {
        SyncSettings();
        if (OverlayVisible)
        {
            _overlayWindow.EnsureShown();
            if (AutoPositionOverlayNearRegion && !_settingsService.Settings.LastRegion.IsEmpty)
            {
                _overlayWindow.PositionNearRegion(_settingsService.Settings.LastRegion);
            }
        }
        _coordinator.Start();
    }

    [RelayCommand]
    public void PauseTranslation()
    {
        _coordinator.Pause();
    }

    [RelayCommand]
    public void StopTranslation()
    {
        _coordinator.Stop();

        // Reset UI presentation to clean empty state
        OriginalText = "Chưa nhận diện được văn bản";
        EnglishText = "Chưa có bản dịch";
        ChineseText = "Chưa có bản dịch";
        VietnameseText = "Chưa có bản dịch";
        OriginalLanguageBadge = "Gốc (Tự động)";
        HasDetectedText = false;
        HasPreviewImage = false;
        PreviewImage = null;
        AverageLatencyMs = 0;
        FormattedLatencyText = "N/A";
        LastUpdatedText = "—";

        CaptureFpsText = "0 FPS";
        OcrLatencyText = "N/A";
        TranslationLatencyText = "N/A";
        EndToEndLatencyText = "N/A";
        OcrQueueLengthText = "0";
        AudioPeakLevel = 0f;
        AudioRmsLevel = 0f;
        FormattedAudioLevel = "0%";
        DiagnosticsTooltipText = "Chưa có dữ liệu đo hiệu năng (Hệ thống đang dừng)";
    }

    private void UpdateDiagnostics()
    {
        CaptureFpsText = $"{_coordinator.CurrentFps} FPS";
        OcrLatencyText = _coordinator.LastOcrLatencyMs > 0 ? $"{_coordinator.LastOcrLatencyMs} ms" : "N/A";
        TranslationLatencyText = _coordinator.LastTranslationLatencyMs > 0 ? $"{_coordinator.LastTranslationLatencyMs} ms" : "N/A";
        EndToEndLatencyText = _coordinator.AverageLatencyMs > 0 ? $"{_coordinator.AverageLatencyMs} ms" : "N/A";
        FormattedLatencyText = _coordinator.AverageLatencyMs > 0 ? $"≈ {_coordinator.AverageLatencyMs} ms" : "N/A";
        CacheHitRateText = $"{(_coordinator.CacheHitRate * 100):0.#}%";
        OcrQueueLengthText = $"{_coordinator.OcrQueueLength}";

        var cap = _coordinator.LastCaptureDiagnostics;
        string capInfo = cap.SourceHwnd != nint.Zero
            ? $"{cap.BackendName} | HWND=0x{cap.SourceHwnd:X} ({cap.SourceWindowTitle}) | Crop={cap.CropWidth}x{cap.CropHeight}"
            : $"{cap.BackendName} | Crop={cap.CropWidth}x{cap.CropHeight}";

        DiagnosticsTooltipText =
            $"📊 HIỆU NĂNG REALTIME (DIAGNOSTICS)\n" +
            $"• Nguồn Capture: {capInfo}\n" +
            $"• Chụp màn hình (Capture): {_coordinator.LastCaptureLatencyMs} ms ({_coordinator.CaptureFrameCount} frames)\n" +
            $"• Nhận diện (OCR): {OcrLatencyText} (Thử: {_coordinator.OcrAttemptCount} | Thành công: {_coordinator.OcrSuccessCount})\n" +
            $"• Dịch thuật (Translation): {TranslationLatencyText}\n" +
            $"• Cập nhật Overlay: {_coordinator.LastOverlayUpdateLatencyMs} ms\n" +
            $"• Tổng độ trễ (End-to-End): {EndToEndLatencyText}\n" +
            $"• Tần số khung hình: {_coordinator.CurrentFps} FPS\n" +
            $"• Tỉ lệ Cache Hit: {(_coordinator.CacheHitRate * 100):0.#}%\n" +
            $"• Hàng đợi OCR: {_coordinator.OcrQueueLength}\n" +
            $"• Trạng thái Pipeline: {_coordinator.PipelineStatusText}";
    }

    [RelayCommand]
    public void SelectRegion()
    {
        var fgTargetWin = _captureService.GetForegroundTargetWindow();
        var mainWin = System.Windows.Application.Current?.MainWindow;
        bool wasMainWindowVisible = mainWin != null
            && mainWin.IsVisible
            && mainWin.WindowState != System.Windows.WindowState.Minimized;

        // Hide Dashboard before opening region selector so ScreenSub AI never occludes the target video
        if (wasMainWindowVisible && mainWin != null)
        {
            mainWin.Hide();
            System.Threading.Thread.Sleep(80);
        }

        _coordinator.NotifySelectingRegion();
        var selector = _regionSelectorFactory();
        selector.ShowDialog();

        if (!selector.IsCancelled && !selector.SelectedRegion.IsEmpty)
        {
            var region = selector.SelectedRegion;
            FormattedRegionSize = region.FormattedSize;
            _settingsService.Settings.LastRegion = region;

            // Detect target window underneath the selected region (e.g. Chrome playing YouTube)
            var detectedWin = _captureService.FindTargetWindowForRegion(region) ?? fgTargetWin;
            if (detectedWin != null)
            {
                RefreshWindows();
                SelectedWindow = OpenWindows.FirstOrDefault(w => w.Handle == detectedWin.Handle) ?? detectedWin;
                _coordinator.UpdateSelectedWindow(SelectedWindow);
                _settingsService.Settings.LastWindowProcessName = detectedWin.ProcessName;
                _settingsService.Settings.LastWindowTitle = detectedWin.Title;
            }

            _settingsService.SaveSettingsAsync();

            // 1. Ensure Overlay is created/shown and position it right near the selected region on video
            _overlayWindow.EnsureShown();
            if (AutoPositionOverlayNearRegion)
            {
                _overlayWindow.PositionNearRegion(region, ignoreLock: true);
            }
            _overlayWindow.ClearSubtitles();
            _overlayWindow.SetOverlayVisible(OverlayVisible);

            // 2. Quick Translate Mode: Auto-start OCR pipeline without bringing Dashboard back over the video!
            if (QuickTranslateMode)
            {
                if (detectedWin != null && detectedWin.Handle != nint.Zero)
                {
                    _captureService.ActivateWindow(detectedWin.Handle);
                }
                SyncSettings();
                _coordinator.UpdateSelectedRegionAndAutoStart(region);
            }
            else
            {
                if (wasMainWindowVisible && mainWin != null)
                {
                    mainWin.Show();
                    mainWin.Activate();
                }
                _coordinator.UpdateSelectedRegion(region);
            }
        }
        else
        {
            if (wasMainWindowVisible && mainWin != null)
            {
                mainWin.Show();
                mainWin.Activate();
            }
            _coordinator.CancelSelectingRegion();
        }
    }

    [RelayCommand]
    public void TestOverlay()
    {
        OverlayVisible = true;
        _overlayWindow.EnsureShown();
        if (!_settingsService.Settings.LastRegion.IsEmpty)
        {
            _overlayWindow.PositionNearRegion(_settingsService.Settings.LastRegion, ignoreLock: true);
        }
        _overlayWindow.ShowTestSubtitle();
        PipelineStatusText = "Đang hiển thị phụ đề thử nghiệm [TEST] trên Overlay";
    }

    [RelayCommand]
    public void ToggleStartPause()
    {
        if (_coordinator.State == SystemState.Translating || _coordinator.State == SystemState.Starting)
        {
            PauseTranslation();
        }
        else
        {
            StartTranslation();
        }
    }

    [RelayCommand]
    public void ClearRegion()
    {
        _coordinator.UpdateSelectedRegion(ScreenRegion.Empty);
        FormattedRegionSize = "Chưa chọn vùng";
        _settingsService.Settings.LastRegion = ScreenRegion.Empty;
        _settingsService.SaveSettingsAsync();

        OriginalText = "Chưa nhận diện được văn bản";
        EnglishText = "Chưa có bản dịch";
        ChineseText = "Chưa có bản dịch";
        VietnameseText = "Chưa có bản dịch";
        OriginalLanguageBadge = "Gốc (Tự động)";
        HasDetectedText = false;
        HasPreviewImage = false;
        PreviewImage = null;
    }

    [RelayCommand]
    public void RefreshWindows()
    {
        OpenWindows.Clear();
        var list = _captureService.GetOpenWindows();
        foreach (var w in list)
        {
            OpenWindows.Add(w);
        }

        if (SelectedWindow == null && OpenWindows.Count > 0)
        {
            SelectedWindow = OpenWindows.FirstOrDefault(w => w.Title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ||
                                                             w.Title.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
                             ?? OpenWindows[0];
        }
    }

    [RelayCommand]
    public void IncreaseFontSize()
    {
        if (OverlayFontSize < 60)
        {
            OverlayFontSize += 2;
            _overlayWindow.UpdateStyling(OverlayFontSize, OverlayTextColor, OverlayBackgroundOpacity);
            SyncSettings();
        }
    }

    [RelayCommand]
    public void DecreaseFontSize()
    {
        if (OverlayFontSize > 16)
        {
            OverlayFontSize -= 2;
            _overlayWindow.UpdateStyling(OverlayFontSize, OverlayTextColor, OverlayBackgroundOpacity);
            SyncSettings();
        }
    }

    [RelayCommand]
    public void ToggleOverlayEditMode()
    {
        IsOverlayEditMode = !IsOverlayEditMode;
        _overlayWindow.SetEditMode(IsOverlayEditMode);
    }

    private void ApplyOnlineAndEngineSettings()
    {
        var s = _settingsService.Settings;
        s.AzureApiKey = AzureApiKey?.Trim() ?? string.Empty;
        s.AzureRegion = AzureRegion?.Trim() ?? string.Empty;
        s.AzureEndpoint = string.IsNullOrWhiteSpace(AzureEndpoint)
            ? "https://api.cognitive.microsofttranslator.com"
            : AzureEndpoint.Trim().TrimEnd('/');
        s.GeminiApiKey = GeminiApiKey?.Trim() ?? string.Empty;
        s.GeminiModel = string.IsNullOrWhiteSpace(GeminiModel) ? "gemini-2.5-flash" : GeminiModel.Trim();
        s.OnlineProvider = OnlineProvider;
        s.EnableContextAwareTranslation = EnableContextAwareTranslation;

        s.UseMockTranslation = UseMockTranslation;
        IsMockMode = UseMockTranslation;

        s.TranslationEngineMode = TranslationEngineMode;
        s.HasExplicitlySelectedOnlineMode = TranslationEngineMode == TranslationEngineMode.Online;
        s.AutoFallbackPolicy = AutoFallbackPolicy;
        s.AllowCloudFallbackOnOfflineFailure = AllowCloudFallbackOnOfflineFailure;
        s.PerformanceProfile = PerformanceProfile;
        s.SelectedBackend = SelectedBackend;

        s.QuickTranslateMode = QuickTranslateMode;
        s.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        s.AutoPositionOverlayNearRegion = AutoPositionOverlayNearRegion;
    }

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        ApplyOnlineAndEngineSettings();
        await _settingsService.SaveSettingsAsync();

        _coordinator.ResetTranslationCacheAndRetranslate();
        ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
        TranslationStatusText = _coordinator.TranslationStatusText;
        UpdateTranslationHealthState();

        SettingsSaveMessage = "Đã lưu cài đặt thành công!";
        await Task.Delay(2500);
        SettingsSaveMessage = string.Empty;
    }

    [RelayCommand]
    public async Task TestOnlineConnectionAsync()
    {
        if (IsTestingConnection) return;
        IsTestingConnection = true;
        ConnectionTestMessage = "Đang kiểm tra kết nối...";
        IsConnectionTestSuccess = false;

        try
        {
            ApplyOnlineAndEngineSettings();
            await _settingsService.SaveSettingsAsync();

            var (success, message) = await _coordinator.TestOnlineConnectionAsync();
            IsConnectionTestSuccess = success;
            ConnectionTestMessage = message;

            _coordinator.ResetTranslationCacheAndRetranslate();
            ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
            TranslationStatusText = _coordinator.TranslationStatusText;
            UpdateTranslationHealthState();
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    [RelayCommand]
    public void StartRecordHotkey()
    {
        IsRecordingHotkey = true;
        HotkeyStatusMessage = "Đang chờ nhấn phím... Nhấn tổ hợp phím mới (Ví dụ: Ctrl + Alt + Q)";
        IsHotkeyError = false;
    }

    [RelayCommand]
    public void CancelRecordHotkey()
    {
        IsRecordingHotkey = false;
        HotkeyStatusMessage = string.Empty;
        IsHotkeyError = false;
    }

    public void ApplyRecordedHotkey(ModifierKeys modifiers, Key key)
    {
        if (!IsRecordingHotkey) return;

        // Skip modifier-only key events
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        // Must have at least one modifier
        if (modifiers == ModifierKeys.None)
        {
            HotkeyStatusMessage = "Tổ hợp phím phải chứa ít nhất một phím bổ trợ (Ctrl, Alt hoặc Shift).";
            IsHotkeyError = true;
            return;
        }

        // Construct display string
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        string candidateText = string.Join(" + ", parts);

        bool success = RequestRegisterHotkey?.Invoke(modifiers, key) ?? false;
        if (success)
        {
            HotkeyDisplayText = candidateText;
            _settingsService.Settings.HotkeyModifiers = (int)modifiers;
            _settingsService.Settings.HotkeyKey = (int)key;
            _settingsService.Settings.HotkeyDisplayText = candidateText;
            _ = _settingsService.SaveSettingsAsync();

            HotkeyStatusMessage = $"Đã cập nhật và kích hoạt phím tắt: {candidateText}";
            IsHotkeyError = false;
            IsRecordingHotkey = false;
        }
        else
        {
            HotkeyStatusMessage = $"Không thể đăng ký '{candidateText}'. Phím này đã bị ứng dụng khác chiếm dụng. Giữ nguyên '{HotkeyDisplayText}'.";
            IsHotkeyError = true;
            IsRecordingHotkey = false;
        }
    }

    [RelayCommand]
    public void ResetDefaultHotkey()
    {
        var defaultModifiers = ModifierKeys.Control | ModifierKeys.Alt;
        var defaultKey = Key.Q;
        const string defaultText = "Ctrl + Alt + Q";

        bool success = RequestRegisterHotkey?.Invoke(defaultModifiers, defaultKey) ?? false;
        if (success)
        {
            HotkeyDisplayText = defaultText;
            _settingsService.Settings.HotkeyModifiers = (int)defaultModifiers;
            _settingsService.Settings.HotkeyKey = (int)defaultKey;
            _settingsService.Settings.HotkeyDisplayText = defaultText;
            _ = _settingsService.SaveSettingsAsync();

            HotkeyStatusMessage = $"Đã khôi phục phím tắt mặc định: {defaultText}";
            IsHotkeyError = false;
        }
        else
        {
            HotkeyStatusMessage = "Không thể đăng ký phím mặc định (Ctrl + Alt + Q) vì đang bị ứng dụng khác chiếm dụng.";
            IsHotkeyError = true;
        }
        IsRecordingHotkey = false;
    }

    [RelayCommand]
    public void SelectTranslationMode(TranslationMode mode)
    {
        CurrentMode = mode;
    }

    [RelayCommand]
    public void RefreshAudioDevices()
    {
        AudioDevices.Clear();
        foreach (var dev in _coordinator.OutputAudioDevices)
        {
            AudioDevices.Add(dev);
        }

        if (SelectedAudioDevice == null || !AudioDevices.Any(d => d.Id == SelectedAudioDevice.Id))
        {
            SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.IsDefault) ?? AudioDevices.FirstOrDefault();
        }
    }

    partial void OnCurrentModeChanged(TranslationMode value)
    {
        _coordinator.SetTranslationMode(value);
        _settingsService.Settings.TranslationMode = value;
        _ = _settingsService.SaveSettingsAsync();
    }

    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo? value)
    {
        _coordinator.SetSelectedAudioDevice(value);
        _settingsService.Settings.SelectedAudioDeviceId = value?.Id;
        _ = _settingsService.SaveSettingsAsync();
    }

    partial void OnSpeechLanguageChanged(string value)
    {
        _settingsService.Settings.SpeechLanguage = value;
        _ = _settingsService.SaveSettingsAsync();
    }

    partial void OnCaptureSourceChanged(CaptureSourceType value)
    {
        _settingsService.Settings.CaptureSource = value;
        _ = _settingsService.SaveSettingsAsync();
    }

    partial void OnSelectedWindowChanged(WindowInfo? value)
    {
        _coordinator.UpdateSelectedWindow(value);
    }

    partial void OnIsFixedRegionModeChanged(bool value)
    {
        if (value)
        {
            IsFollowWindowMode = false;
            _settingsService.Settings.RegionMode = RegionMode.FixedRegion;
        }
    }

    partial void OnIsFollowWindowModeChanged(bool value)
    {
        if (value)
        {
            IsFixedRegionMode = false;
            _settingsService.Settings.RegionMode = RegionMode.FollowWindow;
        }
    }

    partial void OnDisplayModeChanged(DisplayMode value)
    {
        _settingsService.Settings.DisplayMode = value;
        _overlayWindow.UpdateDisplayMode(value);
    }

    partial void OnOverlayVisibleChanged(bool value)
    {
        _settingsService.Settings.OverlayVisible = value;
        _ = _settingsService.SaveSettingsAsync();
        _overlayWindow.SetOverlayVisible(value);
    }

    public void CloseOverlay()
    {
        _overlayWindow.SetOverlayVisible(false);
        _overlayWindow.Close();
    }

    partial void OnOverlayBackgroundOpacityChanged(double value)
    {
        _overlayWindow.UpdateStyling(OverlayFontSize, OverlayTextColor, value);
    }

    partial void OnOverlayLockedChanged(bool value)
    {
        _overlayWindow.SetLocked(value);
    }

    partial void OnOverlayClickThroughChanged(bool value)
    {
        _overlayWindow.SetClickThrough(value);
    }

    partial void OnOverlayAlwaysOnTopChanged(bool value)
    {
        _overlayWindow.Topmost = value;
    }

    partial void OnOverlayWidthChanged(double value)
    {
        _overlayWindow.UpdateWidth(value);
        _settingsService.Settings.OverlayWidth = value;
    }

    [RelayCommand]
    public void DismissFirstRunBanner()
    {
        ShowFirstRunBanner = false;
        _settingsService.Settings.HasCompletedFirstRunGuide = true;
        _ = _settingsService.SaveSettingsAsync();
    }

    partial void OnSourceLanguageChanged(SourceLanguage value)
    {
        SyncSettings();
        _ = _settingsService.SaveSettingsAsync();
        ClearSubtitlesOnSettingChange();
    }

    partial void OnTargetEnglishChanged(bool value)
    {
        SyncSettings();
        _ = _settingsService.SaveSettingsAsync();
        ClearSubtitlesOnSettingChange();
    }

    partial void OnTargetChineseChanged(bool value)
    {
        SyncSettings();
        _ = _settingsService.SaveSettingsAsync();
        ClearSubtitlesOnSettingChange();
    }

    partial void OnTargetVietnameseChanged(bool value)
    {
        SyncSettings();
        _ = _settingsService.SaveSettingsAsync();
        ClearSubtitlesOnSettingChange();
    }

    private void ClearSubtitlesOnSettingChange()
    {
        OriginalText = "Chưa nhận diện được văn bản";
        EnglishText = "Chưa có bản dịch";
        ChineseText = "Chưa có bản dịch";
        VietnameseText = "Chưa có bản dịch";
        HasDetectedText = false;
        DetectedLanguageCode = "auto";
        UpdateTranslationRowVisibility();
        _overlayWindow.ClearSubtitles();
        _coordinator.ResetTranslationCacheAndRetranslate();
    }

    public void UpdateTranslationRowVisibility()
    {
        if (!HasDetectedText)
        {
            ShowEnglishTranslationRow = TargetEnglish;
            ShowChineseTranslationRow = TargetChinese;
            ShowVietnameseTranslationRow = TargetVietnamese;
        }
        else
        {
            ShowEnglishTranslationRow = TargetEnglish && DetectedLanguageCode != "en";
            ShowChineseTranslationRow = TargetChinese && DetectedLanguageCode != "zh";
            ShowVietnameseTranslationRow = TargetVietnamese && DetectedLanguageCode != "vi";
        }
    }

    private void SyncSettings()
    {
        var s = _settingsService.Settings;
        s.CaptureSource = CaptureSource;
        s.SourceLanguage = SourceLanguage;
        s.DisplayMode = DisplayMode;
        s.OverlayFontSize = OverlayFontSize;
        s.OverlayTextColor = OverlayTextColor;
        s.OverlayBackgroundOpacity = OverlayBackgroundOpacity;
        s.OverlayLocked = OverlayLocked;
        s.OverlayClickThrough = OverlayClickThrough;
        s.OverlayAlwaysOnTop = OverlayAlwaysOnTop;
        s.OverlayVisible = OverlayVisible;
        s.OverlayWidth = OverlayWidth;
        s.UseMockTranslation = UseMockTranslation;
        IsMockMode = UseMockTranslation;

        var targets = new List<TargetLanguage>();
        if (TargetEnglish) targets.Add(TargetLanguage.English);
        if (TargetChinese) targets.Add(TargetLanguage.Chinese);
        if (TargetVietnamese) targets.Add(TargetLanguage.Vietnamese);
        s.TargetLanguages = targets;

        s.TranslationMode = CurrentMode;
        s.SpeechLanguage = SpeechLanguage;
        s.SelectedAudioDeviceId = SelectedAudioDevice?.Id;

        s.QuickTranslateMode = QuickTranslateMode;
        s.MinimizeToTrayOnClose = MinimizeToTrayOnClose;
        s.AutoPositionOverlayNearRegion = AutoPositionOverlayNearRegion;

        ApplyOnlineAndEngineSettings();

        _ = _settingsService.SaveSettingsAsync();
    }

    private void LoadHardwareInfo()
    {
        if (_hardwareDetector == null) return;
        try
        {
            var info = _hardwareDetector.DetectHardware();
            CpuInfoText = $"{info.CpuName} ({info.PhysicalCores}C / {info.LogicalProcessors}T, AVX2: {(info.SupportsAvx2 ? "Có" : "Không")})";
            GpuInfoText = $"{info.GpuName} (VRAM: {info.FormattedVram}, Vendor: {info.GpuVendor})";
            RamInfoText = $"{info.FormattedRam} (Khả dụng: {info.AvailableRamBytes / (1024.0 * 1024 * 1024):0.#} GB)";
            RecommendedBackendText = info.RecommendedBackend.ToString();
            FreeDiskSpaceText = info.FormattedDiskSpace;
        }
        catch (Exception ex)
        {
            CpuInfoText = "Lỗi phát hiện phần cứng";
            GpuInfoText = ex.Message;
        }
    }

    public void RefreshModelList()
    {
        if (_modelManager == null) return;
        try
        {
            ModelStorageDirectory = _modelManager.GetModelStorageDirectory();
            Models.Clear();
            foreach (var m in _modelManager.GetModels())
            {
                Models.Add(m);
            }
        }
        catch { }
    }

    [RelayCommand]
    public async Task InstallModelAsync(ModelMetadata? model)
    {
        if (model == null || _modelManager == null) return;
        IsModelBusy = true;
        ModelActionMessage = $"Đang tải và chuẩn bị mô hình {model.Name}...";
        try
        {
            bool ok = await _modelManager.InstallModelAsync(model.Id);
            ModelActionMessage = ok ? $"Đã cài đặt thành công {model.Name}!" : $"Cài đặt {model.Name} thất bại.";
            RefreshModelList();
        }
        finally
        {
            IsModelBusy = false;
        }
    }

    [RelayCommand]
    public async Task DeleteModelAsync(ModelMetadata? model)
    {
        if (model == null || _modelManager == null) return;
        IsModelBusy = true;
        try
        {
            bool ok = await _modelManager.DeleteModelAsync(model.Id);
            ModelActionMessage = ok ? $"Đã xóa mô hình {model.Name}." : $"Không thể xóa {model.Name}.";
            RefreshModelList();
        }
        finally
        {
            IsModelBusy = false;
        }
    }

    [RelayCommand]
    public async Task VerifyModelAsync(ModelMetadata? model)
    {
        if (model == null || _modelManager == null) return;
        IsModelBusy = true;
        ModelActionMessage = $"Đang kiểm tra toàn vẹn SHA-256 cho {model.Name}...";
        try
        {
            bool ok = await _modelManager.VerifyModelIntegrityAsync(model.Id);
            ModelActionMessage = ok ? $"Mô hình {model.Name} toàn vẹn, hợp lệ!" : $"Mô hình {model.Name} không tìm thấy hoặc bị lỗi.";
            RefreshModelList();
        }
        finally
        {
            IsModelBusy = false;
        }
    }

    [RelayCommand]
    public async Task ActivateModelAsync(ModelMetadata? model)
    {
        if (model == null || _modelManager == null) return;
        IsModelBusy = true;
        try
        {
            bool ok = await _modelManager.ActivateModelAsync(model.Id);
            ModelActionMessage = ok ? $"Đã kích hoạt mô hình {model.Name}!" : $"Không thể kích hoạt {model.Name}.";
            RefreshModelList();
        }
        finally
        {
            IsModelBusy = false;
        }
    }

    [RelayCommand]
    public void SetPerformanceProfile(OverallPerformanceProfile profile)
    {
        PerformanceProfile = profile;
        var s = _settingsService.Settings;
        s.PerformanceProfile = profile;

        switch (profile)
        {
            case OverallPerformanceProfile.Fast:
                s.CaptureFps = 3;
                s.CpuWorkerThreads = 2;
                s.VadMinSpeechDurationMs = 250;
                break;
            case OverallPerformanceProfile.Balanced:
                s.CaptureFps = 5;
                s.CpuWorkerThreads = 4;
                s.VadMinSpeechDurationMs = 300;
                break;
            case OverallPerformanceProfile.Quality:
                s.CaptureFps = 8;
                s.CpuWorkerThreads = 6;
                s.VadMinSpeechDurationMs = 400;
                break;
        }

        _ = _settingsService.SaveSettingsAsync();
    }

    partial void OnOnlineProviderIndexChanged(int value)
    {
        OnlineProvider = value == 1 ? OnlineTranslationProviderType.Gemini : OnlineTranslationProviderType.Azure;
        if (_isLoadingSettings) return;

        _settingsService.Settings.OnlineProvider = OnlineProvider;
        _coordinator.ResetTranslationCacheAndRetranslate();
        ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
        TranslationStatusText = _coordinator.TranslationStatusText;
        UpdateTranslationHealthState();
        ConnectionTestMessage = string.Empty;
        _ = _settingsService.SaveSettingsAsync();
    }

    [RelayCommand]
    public void SetEngineMode(TranslationEngineMode mode)
    {
        if (TranslationEngineMode != mode)
        {
            TranslationEngineMode = mode;
            return;
        }

        // Same mode clicked again: force refresh badge/status and guide user if Online key is missing
        _coordinator.ResetTranslationCacheAndRetranslate();
        ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
        TranslationStatusText = _coordinator.TranslationStatusText;
        UpdateTranslationHealthState();
    }

    partial void OnTranslationEngineModeChanged(TranslationEngineMode value)
    {
        if (_isLoadingSettings) return;

        _settingsService.Settings.TranslationEngineMode = value;
        _settingsService.Settings.HasExplicitlySelectedOnlineMode = value == TranslationEngineMode.Online;
        _coordinator.ResetTranslationCacheAndRetranslate();
        ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
        TranslationStatusText = _coordinator.TranslationStatusText;
        UpdateTranslationHealthState();
        _ = _settingsService.SaveSettingsAsync();
    }

    partial void OnUseMockTranslationChanged(bool value)
    {
        if (_isLoadingSettings) return;

        _settingsService.Settings.UseMockTranslation = value;
        IsMockMode = value;
        _coordinator.ResetTranslationCacheAndRetranslate();
        ActiveProviderBadgeText = _coordinator.ActiveProviderBadgeText;
        TranslationStatusText = _coordinator.TranslationStatusText;
        UpdateTranslationHealthState();
        _ = _settingsService.SaveSettingsAsync();
    }

    private static BitmapSource? LoadBitmapFromBytes(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
