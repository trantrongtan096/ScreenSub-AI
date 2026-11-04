using System.Collections.ObjectModel;
using System.IO;
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
    private string _translationStatusText = "Kết nối";

    [ObservableProperty]
    private long _averageLatencyMs = 420;

    [ObservableProperty]
    private string _formattedRegionSize = "1280 × 160";

    // Capture & Region Controls
    [ObservableProperty]
    private CaptureSourceType _captureSource = CaptureSourceType.Window;

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
    private SourceLanguage _sourceLanguage = SourceLanguage.Auto;

    [ObservableProperty]
    private bool _targetEnglish = true;

    [ObservableProperty]
    private bool _targetChinese = true;

    [ObservableProperty]
    private bool _targetVietnamese = true;

    [ObservableProperty]
    private DisplayMode _displayMode = DisplayMode.Triple;

    // Live Preview
    [ObservableProperty]
    private BitmapSource? _previewImage;

    // Realtime Subtitles
    [ObservableProperty]
    private string _originalText = "你今天有时间吗？";

    [ObservableProperty]
    private string _englishText = "Are you free today?";

    [ObservableProperty]
    private string _vietnameseText = "Hôm nay bạn có rảnh không?";

    [ObservableProperty]
    private string _lastUpdatedText = DateTime.Now.ToString("HH:mm:ss");

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

    // Settings Tab Values
    [ObservableProperty]
    private string _azureApiKey = string.Empty;

    [ObservableProperty]
    private string _azureRegion = "eastasia";

    [ObservableProperty]
    private bool _useMockTranslation = true;

    [ObservableProperty]
    private string _settingsSaveMessage = string.Empty;

    public MainViewModel(
        IScreenCaptureService captureService,
        ISubtitleCoordinator coordinator,
        ISettingsService settingsService,
        SubtitleOverlayWindow overlayWindow,
        Func<Views.RegionSelectionWindow> regionSelectorFactory)
    {
        _captureService = captureService;
        _coordinator = coordinator;
        _settingsService = settingsService;
        _overlayWindow = overlayWindow;
        _regionSelectorFactory = regionSelectorFactory;

        LoadSettingsIntoProperties();
        RefreshWindows();

        // Wire coordinator events
        _coordinator.StateChanged += (_, s) =>
        {
            State = s;
            OcrStatusText = s == SystemState.Running ? "Đang chạy" : "Sẵn sàng";
        };

        _coordinator.LatencyUpdated += (_, lat) => AverageLatencyMs = lat;

        _coordinator.FrameCaptured += (_, frameBytes) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                PreviewImage = LoadBitmapFromBytes(frameBytes);
            });
        };

        _coordinator.SubtitleUpdated += (_, item) =>
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                OriginalText = item.ChineseText.Length > 0 ? item.ChineseText : item.OriginalText;
                EnglishText = item.EnglishText;
                VietnameseText = item.VietnameseText;
                LastUpdatedText = item.UpdatedAt.ToLocalTime().ToString("HH:mm:ss");

                _overlayWindow.UpdateSubtitle(item);
            });
        };

        // Initialize overlay
        _overlayWindow.UpdateStyling(OverlayFontSize, OverlayTextColor, OverlayBackgroundOpacity);
        _overlayWindow.UpdateDisplayMode(DisplayMode);
        _overlayWindow.Show();
    }

    private void LoadSettingsIntoProperties()
    {
        var s = _settingsService.Settings;
        SourceLanguage = s.SourceLanguage;
        DisplayMode = s.DisplayMode;
        OverlayFontSize = s.OverlayFontSize;
        OverlayTextColor = s.OverlayTextColor;
        OverlayBackgroundOpacity = s.OverlayBackgroundOpacity;
        OverlayLocked = s.OverlayLocked;
        OverlayClickThrough = s.OverlayClickThrough;
        OverlayAlwaysOnTop = s.OverlayAlwaysOnTop;
        OverlayVisible = s.OverlayVisible;
        FormattedRegionSize = s.LastRegion.FormattedSize;

        AzureApiKey = s.AzureApiKey;
        AzureRegion = s.AzureRegion;
        UseMockTranslation = s.UseMockTranslation;

        IsFixedRegionMode = s.RegionMode == RegionMode.FixedRegion;
        IsFollowWindowMode = s.RegionMode == RegionMode.FollowWindow;
    }

    [RelayCommand]
    public void Navigate(string tab)
    {
        CurrentTab = tab;
    }

    [RelayCommand]
    public void StartTranslation()
    {
        SyncSettings();
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
    }

    [RelayCommand]
    public void SelectRegion()
    {
        var selector = _regionSelectorFactory();
        selector.ShowDialog();

        if (!selector.IsCancelled && !selector.SelectedRegion.IsEmpty)
        {
            _coordinator.UpdateSelectedRegion(selector.SelectedRegion);
            FormattedRegionSize = selector.SelectedRegion.FormattedSize;
            _settingsService.Settings.LastRegion = selector.SelectedRegion;
            _settingsService.SaveSettingsAsync();
        }
    }

    [RelayCommand]
    public void ClearRegion()
    {
        _coordinator.UpdateSelectedRegion(ScreenRegion.Empty);
        FormattedRegionSize = "Chưa chọn";
        _settingsService.Settings.LastRegion = ScreenRegion.Empty;
        _settingsService.SaveSettingsAsync();
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

    [RelayCommand]
    public async Task SaveSettingsAsync()
    {
        var s = _settingsService.Settings;
        s.AzureApiKey = AzureApiKey;
        s.AzureRegion = AzureRegion;
        s.UseMockTranslation = UseMockTranslation;
        await _settingsService.SaveSettingsAsync();

        SettingsSaveMessage = "Đã lưu cài đặt thành công!";
        await Task.Delay(2500);
        SettingsSaveMessage = string.Empty;
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
        if (value) _overlayWindow.Show();
        else _overlayWindow.Hide();
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

    private void SyncSettings()
    {
        var s = _settingsService.Settings;
        s.SourceLanguage = SourceLanguage;
        s.DisplayMode = DisplayMode;
        s.OverlayFontSize = OverlayFontSize;
        s.OverlayTextColor = OverlayTextColor;
        s.OverlayBackgroundOpacity = OverlayBackgroundOpacity;
        s.OverlayLocked = OverlayLocked;
        s.OverlayClickThrough = OverlayClickThrough;
        s.OverlayAlwaysOnTop = OverlayAlwaysOnTop;
        s.OverlayVisible = OverlayVisible;

        var targets = new List<TargetLanguage>();
        if (TargetEnglish) targets.Add(TargetLanguage.English);
        if (TargetChinese) targets.Add(TargetLanguage.Chinese);
        if (TargetVietnamese) targets.Add(TargetLanguage.Vietnamese);
        s.TargetLanguages = targets;

        _settingsService.SaveSettingsAsync();
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
