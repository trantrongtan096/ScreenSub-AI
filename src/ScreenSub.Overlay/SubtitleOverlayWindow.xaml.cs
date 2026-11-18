using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Overlay;

public partial class SubtitleOverlayWindow : Window
{
    private nint _hwnd;
    private bool _isWindowShown;
    private bool _isClosed;
    private bool _isEditMode;
    private bool _isOverlayVisible = true;
    private bool _clickThrough = true;
    private bool _isLocked;
    private bool _isShowingTestSubtitle;
    private DisplayMode _displayMode = DisplayMode.Triple;
    private double _baseFontSize = 24.0;
    private string _textColorHex = "#FFFFFF";
    private SubtitleItem _currentItem = SubtitleItem.Empty;

    public bool IsEditMode => _isEditMode;
    public bool IsOverlayVisible => _isOverlayVisible;
    public bool IsShowingTestSubtitle => _isShowingTestSubtitle;
    public bool IsContainerVisible => OverlayContainer.Visibility == Visibility.Visible;
    public long LastUpdateLatencyMs { get; private set; }
    public nint Handle => _hwnd;

    public SubtitleOverlayWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _isClosed = true;
    }

    private int _lastAppliedExStyle = 0;
    private static readonly SolidColorBrush DefaultWhiteBrush = CreateFrozenBrush(Colors.White);
    private static readonly SolidColorBrush TripleOrigBrush = CreateFrozenBrush(Color.FromRgb(148, 163, 184));
    private static readonly SolidColorBrush DualOrigBrush = CreateFrozenBrush(Color.FromRgb(203, 213, 225));
    private static readonly SolidColorBrush SecondaryAmberBrush = CreateFrozenBrush(Color.FromRgb(253, 224, 71));
    private string _cachedBrushHex = "#FFFFFF";
    private SolidColorBrush _cachedUserBrush = DefaultWhiteBrush;

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Ensures the WPF Overlay Window has been initialized and shown at the OS level
    /// without stealing keyboard/foreground focus from the target application (e.g. Chrome).
    /// </summary>
    public void EnsureShown()
    {
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            if (!_isWindowShown)
            {
                _isWindowShown = true;
                ShowActivated = false;
                try
                {
                    Show();
                }
                catch { }

                if (_hwnd == nint.Zero)
                {
                    _hwnd = new WindowInteropHelper(this).Handle;
                }

                if (_hwnd != nint.Zero)
                {
                    try
                    {
                        OverlayNativeMethods.SetWindowDisplayAffinity(_hwnd, OverlayNativeMethods.WDA_EXCLUDEFROMCAPTURE);
                    }
                    catch { }
                    ApplyClickThrough();
                }
            }
            else
            {
                if (Visibility != Visibility.Visible)
                {
                    Visibility = Visibility.Visible;
                }
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _isWindowShown = true;

        // Exclude this overlay window from screen capture to prevent infinite OCR loop
        try
        {
            OverlayNativeMethods.SetWindowDisplayAffinity(_hwnd, OverlayNativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }
        catch { }

        // Initially collapsed until first valid subtitle arrives, test overlay is triggered, or edit mode activated
        if (!_isEditMode && !_isShowingTestSubtitle && !_currentItem.HasContent)
        {
            OverlayContainer.Visibility = Visibility.Collapsed;
        }

        ApplyClickThrough();
        EnsureWithinScreenBounds();

        if (!_isOverlayVisible)
        {
            SetOverlayVisible(false);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _isEditMode && !_isLocked)
        {
            try
            {
                DragMove();
            }
            catch { }
        }
    }

    public void SetOverlayVisible(bool visible)
    {
        _isOverlayVisible = visible;
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            if (!visible)
            {
                OverlayContainer.Visibility = Visibility.Collapsed;
                if (_isWindowShown)
                {
                    Visibility = Visibility.Collapsed;
                }
                if (_hwnd != nint.Zero)
                {
                    try
                    {
                        OverlayNativeMethods.ShowWindow(_hwnd, OverlayNativeMethods.SW_HIDE);
                    }
                    catch { }
                }
            }
            else
            {
                EnsureShown();
                Visibility = Visibility.Visible;
                if (_hwnd != nint.Zero)
                {
                    try
                    {
                        OverlayNativeMethods.ShowWindow(_hwnd, OverlayNativeMethods.SW_SHOWNA);
                        EnsureTopmost();
                    }
                    catch { }
                }

                if (_isEditMode || _isShowingTestSubtitle || HasValidDisplayableSubtitle(_currentItem))
                {
                    OverlayContainer.Visibility = Visibility.Visible;
                }
                else
                {
                    OverlayContainer.Visibility = Visibility.Collapsed;
                }
            }
        });
    }

    public void SetEditMode(bool isEdit)
    {
        _isEditMode = isEdit;
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            EnsureShown();
            EditModeBanner.Visibility = isEdit ? Visibility.Visible : Visibility.Collapsed;
            if (isEdit)
            {
                _isShowingTestSubtitle = false;
                Visibility = Visibility.Visible;
                OverlayContainer.Visibility = Visibility.Visible;
                EnsureWithinScreenBounds();

                if (_hwnd != nint.Zero)
                {
                    try
                    {
                        OverlayNativeMethods.ShowWindow(_hwnd, OverlayNativeMethods.SW_SHOWNA);
                        EnsureTopmost();
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(VietnameseTextBlock.Text))
                {
                    VietnameseTextBlock.Visibility = Visibility.Visible;
                    VietnameseTextBlock.Text = "[Vị trí hiển thị phụ đề nổi ScreenSub AI]";
                }
            }
            else
            {
                if (VietnameseTextBlock.Text == "[Vị trí hiển thị phụ đề nổi ScreenSub AI]")
                {
                    VietnameseTextBlock.Text = string.Empty;
                }

                if (!_isOverlayVisible || (!HasValidDisplayableSubtitle(_currentItem) && !_isShowingTestSubtitle))
                {
                    OverlayContainer.Visibility = Visibility.Collapsed;
                    if (!_isOverlayVisible)
                    {
                        Visibility = Visibility.Collapsed;
                        if (_hwnd != nint.Zero)
                        {
                            try
                            {
                                OverlayNativeMethods.ShowWindow(_hwnd, OverlayNativeMethods.SW_HIDE);
                            }
                            catch { }
                        }
                    }
                }
            }
            ApplyClickThrough();
        });
    }

    /// <summary>
    /// Diagnostic Test Overlay feature (Section 8):
    /// Proactively displays a clearly labelled [TEST] subtitle to verify that the Overlay window
    /// can appear on screen and render above other windows.
    /// </summary>
    public void ShowTestSubtitle()
    {
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            _isShowingTestSubtitle = true;
            _isOverlayVisible = true;
            EnsureShown();
            EnsureWithinScreenBounds();

            Visibility = Visibility.Visible;
            OverlayContainer.Visibility = Visibility.Visible;
            EditModeBanner.Visibility = Visibility.Collapsed;

            OriginalTextBlock.Visibility = Visibility.Visible;
            OriginalTextBlock.Text = "[TEST OVERLAY — KIỂM TRA HIỂN THỊ CỬA SỔ PHỤ ĐỀ NỔI]";
            OriginalTextBlock.FontSize = Math.Max(15, _baseFontSize * 0.72);
            OriginalTextBlock.FontWeight = FontWeights.SemiBold;
            OriginalTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248)); // Cyan #38BDF8

            EnglishTextBlock.Visibility = Visibility.Collapsed;
            EnglishTextBlock.Text = string.Empty;

            VietnameseTextBlock.Visibility = Visibility.Visible;
            VietnameseTextBlock.Text = "[TEST] Cửa sổ Subtitle Overlay đang hiển thị bình thường trên màn hình.";
            VietnameseTextBlock.FontSize = Math.Max(22, _baseFontSize);
            VietnameseTextBlock.FontWeight = FontWeights.Bold;
            VietnameseTextBlock.Foreground = new SolidColorBrush(Colors.White);

            if (_hwnd != nint.Zero)
            {
                try
                {
                    OverlayNativeMethods.ShowWindow(_hwnd, OverlayNativeMethods.SW_SHOWNA);
                    EnsureTopmost();
                }
                catch { }
            }
        });
    }

    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        ApplyClickThrough();
    }

    public void SetLocked(bool locked)
    {
        _isLocked = locked;
        if (_isLocked && _isEditMode)
        {
            SetEditMode(false);
        }
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == nint.Zero) return;

        int currentExStyle = OverlayNativeMethods.GetWindowLong(_hwnd, OverlayNativeMethods.GWL_EXSTYLE);
        int targetExStyle = currentExStyle;

        // In edit mode, click-through MUST be disabled so user can drag window
        if (_isEditMode || !_clickThrough)
        {
            targetExStyle &= ~OverlayNativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            targetExStyle |= OverlayNativeMethods.WS_EX_TRANSPARENT;
        }

        targetExStyle |= OverlayNativeMethods.WS_EX_LAYERED | OverlayNativeMethods.WS_EX_TOOLWINDOW | OverlayNativeMethods.WS_EX_NOACTIVATE;

        if (targetExStyle != currentExStyle || targetExStyle != _lastAppliedExStyle)
        {
            _lastAppliedExStyle = targetExStyle;
            OverlayNativeMethods.SetWindowLong(_hwnd, OverlayNativeMethods.GWL_EXSTYLE, targetExStyle);
        }
    }

    private void EnsureTopmost()
    {
        if (_hwnd == nint.Zero || !Topmost) return;
        try
        {
            // Do NOT include SWP_SHOWWINDOW on an already-shown window, as it forces DWM surface invalidation and causes blinking!
            OverlayNativeMethods.SetWindowPos(
                _hwnd,
                OverlayNativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                OverlayNativeMethods.SWP_NOMOVE | OverlayNativeMethods.SWP_NOSIZE | OverlayNativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    public static bool HasValidDisplayableSubtitle(SubtitleItem? item)
    {
        if (item == null || !item.HasContent) return false;

        // Check that at least one target translation is valid and not an unconfigured error marker
        static bool IsValidTarget(string? s) =>
            !string.IsNullOrWhiteSpace(s) &&
            !s.StartsWith("[MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("[Azure chưa cấu hình", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("[Chưa cấu hình", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("[Lỗi dịch", StringComparison.OrdinalIgnoreCase) &&
            s != "—";

        return IsValidTarget(item.VietnameseText) ||
               IsValidTarget(item.EnglishText) ||
               IsValidTarget(item.ChineseText);
    }

    public void UpdateSubtitle(SubtitleItem item)
    {
        var sw = Stopwatch.StartNew();
        _currentItem = item;
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            _isShowingTestSubtitle = false;

            // If user has toggled overlay off, NEVER show subtitle container
            if (!_isOverlayVisible)
            {
                if (OverlayContainer.Visibility != Visibility.Collapsed)
                {
                    OverlayContainer.Visibility = Visibility.Collapsed;
                }
                sw.Stop();
                LastUpdateLatencyMs = sw.ElapsedMilliseconds;
                return;
            }

            if (!HasValidDisplayableSubtitle(item))
            {
                // Only blank out if explicitly empty (e.g. from ClearSubtitles/Stop), do NOT collapse on transient empty
                if (item == null || (!item.HasContent && string.IsNullOrEmpty(item.OriginalText)))
                {
                    SetBlock(OriginalTextBlock, string.Empty, Visibility.Collapsed);
                    SetBlock(EnglishTextBlock, string.Empty, Visibility.Collapsed);
                    SetBlock(VietnameseTextBlock, string.Empty, Visibility.Collapsed);
                    if (!_isEditMode)
                    {
                        OverlayContainer.Visibility = Visibility.Collapsed;
                    }
                }
                sw.Stop();
                LastUpdateLatencyMs = sw.ElapsedMilliseconds;
                return;
            }

            EnsureShown();
            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }
            if (OverlayContainer.Visibility != Visibility.Visible)
            {
                OverlayContainer.Visibility = Visibility.Visible;
            }

            ApplyDisplayMode();
            EnsureTopmost();

            sw.Stop();
            LastUpdateLatencyMs = Math.Max(1, sw.ElapsedMilliseconds);
        });
    }

    private static void SetBlock(TextBlock block, string text, Visibility visibility)
    {
        if (block.Visibility != visibility)
        {
            block.Visibility = visibility;
        }
        if (block.Text != text)
        {
            block.Text = text;
        }
    }

    public void UpdateDisplayMode(DisplayMode mode)
    {
        _displayMode = mode;
        if (_isClosed) return;
        Dispatcher.Invoke(ApplyDisplayMode);
    }

    private void ApplyDisplayMode()
    {
        var item = _currentItem;
        if (!HasValidDisplayableSubtitle(item)) return;

        string sourceText = item.OriginalText?.Trim() ?? string.Empty;
        string srcLang = (item.OriginalLanguageCode ?? "auto").ToLowerInvariant();

        static bool IsUsable(string? t) =>
            !string.IsNullOrWhiteSpace(t) &&
            t != "—" &&
            !t.StartsWith("[MODEL NOT INSTALLED", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("[Azure chưa cấu hình", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("[Chưa cấu hình", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("[Lỗi dịch", StringComparison.OrdinalIgnoreCase);

        // Select primary target translation (prioritizing translated target languages distinct from source language)
        string primaryTarget = string.Empty;
        if (srcLang != "vi" && IsUsable(item.VietnameseText))
        {
            primaryTarget = item.VietnameseText;
        }
        else if (srcLang != "en" && IsUsable(item.EnglishText))
        {
            primaryTarget = item.EnglishText;
        }
        else if (!srcLang.StartsWith("zh") && IsUsable(item.ChineseText))
        {
            primaryTarget = item.ChineseText;
        }
        else if (IsUsable(item.VietnameseText))
        {
            primaryTarget = item.VietnameseText;
        }
        else if (IsUsable(item.EnglishText))
        {
            primaryTarget = item.EnglishText;
        }
        else if (IsUsable(item.ChineseText))
        {
            primaryTarget = item.ChineseText;
        }

        if (string.IsNullOrWhiteSpace(primaryTarget))
        {
            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                primaryTarget = sourceText;
            }
            else
            {
                OverlayContainer.Visibility = Visibility.Collapsed;
                return;
            }
        }

        // Select secondary target translation (distinct from both sourceText and primaryTarget)
        string secondaryTarget = string.Empty;
        if (IsUsable(item.EnglishText) && item.EnglishText != primaryTarget && item.EnglishText != sourceText)
        {
            secondaryTarget = item.EnglishText;
        }
        else if (IsUsable(item.ChineseText) && item.ChineseText != primaryTarget && item.ChineseText != sourceText)
        {
            secondaryTarget = item.ChineseText;
        }
        else if (IsUsable(item.VietnameseText) && item.VietnameseText != primaryTarget && item.VietnameseText != sourceText)
        {
            secondaryTarget = item.VietnameseText;
        }

        if (!string.Equals(_cachedBrushHex, _textColorHex, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(_textColorHex);
                _cachedUserBrush = CreateFrozenBrush(c);
                _cachedBrushHex = _textColorHex;
            }
            catch
            {
                _cachedUserBrush = DefaultWhiteBrush;
                _cachedBrushHex = "#FFFFFF";
            }
        }
        SolidColorBrush userBrush = _cachedUserBrush;

        switch (_displayMode)
        {
            case DisplayMode.Single:
                SetBlock(OriginalTextBlock, string.Empty, Visibility.Collapsed);
                SetBlock(EnglishTextBlock, string.Empty, Visibility.Collapsed);

                SetBlock(VietnameseTextBlock, primaryTarget, Visibility.Visible);
                VietnameseTextBlock.FontSize = Math.Max(24, _baseFontSize * 1.15);
                VietnameseTextBlock.FontWeight = FontWeights.Bold;
                VietnameseTextBlock.Foreground = userBrush;
                break;

            case DisplayMode.Dual:
                var origVis = !string.IsNullOrWhiteSpace(sourceText) && sourceText != primaryTarget
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SetBlock(OriginalTextBlock, sourceText, origVis);
                OriginalTextBlock.FontSize = Math.Max(16, _baseFontSize * 0.78);
                OriginalTextBlock.FontWeight = FontWeights.SemiBold;
                OriginalTextBlock.Foreground = DualOrigBrush;

                SetBlock(EnglishTextBlock, string.Empty, Visibility.Collapsed);

                SetBlock(VietnameseTextBlock, primaryTarget, Visibility.Visible);
                VietnameseTextBlock.FontSize = Math.Max(22, _baseFontSize);
                VietnameseTextBlock.FontWeight = FontWeights.Bold;
                VietnameseTextBlock.Foreground = userBrush;
                break;

            case DisplayMode.Triple:
            default:
                var tripleOrigVis = !string.IsNullOrWhiteSpace(sourceText) && sourceText != primaryTarget
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                SetBlock(OriginalTextBlock, sourceText, tripleOrigVis);
                OriginalTextBlock.FontSize = Math.Max(15, _baseFontSize * 0.72);
                OriginalTextBlock.FontWeight = FontWeights.Medium;
                OriginalTextBlock.Foreground = TripleOrigBrush;

                var secVis = !string.IsNullOrWhiteSpace(secondaryTarget) ? Visibility.Visible : Visibility.Collapsed;
                SetBlock(EnglishTextBlock, secondaryTarget, secVis);
                EnglishTextBlock.FontSize = Math.Max(19, _baseFontSize * 0.88);
                EnglishTextBlock.FontWeight = FontWeights.SemiBold;
                EnglishTextBlock.Foreground = SecondaryAmberBrush;

                SetBlock(VietnameseTextBlock, primaryTarget, Visibility.Visible);
                VietnameseTextBlock.FontSize = Math.Max(22, _baseFontSize);
                VietnameseTextBlock.FontWeight = FontWeights.Bold;
                VietnameseTextBlock.Foreground = userBrush;
                break;
        }
    }

    public void UpdateStyling(double fontSize, string textColorHex, double backgroundOpacity)
    {
        _baseFontSize = fontSize;
        _textColorHex = textColorHex;
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            // Update Background Opacity with modern dark acrylic tone (#0F172A)
            byte alpha = (byte)Math.Clamp((int)(backgroundOpacity * 255), 0, 255);
            OverlayContainer.Background = new SolidColorBrush(Color.FromArgb(alpha, 15, 23, 42));

            ApplyDisplayMode();
        });
    }

    /// <summary>
    /// Updates the maximum and target width of the subtitle overlay, keeping text wrapping and centering clean.
    /// </summary>
    public void UpdateWidth(double width)
    {
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            if (width >= 400 && width <= 2200)
            {
                double maxDipW = SystemParameters.VirtualScreenWidth > 0 ? SystemParameters.VirtualScreenWidth - 20 : 1900;
                double clampedW = Math.Min(width, maxDipW);
                Width = clampedW;
                OverlayContainer.MaxWidth = Math.Max(360, clampedW - 24);
                EnsureWithinScreenBounds();
            }
        });
    }

    /// <summary>
    /// Positions the overlay window right near the selected OCR subtitle region (immediately below or above),
    /// properly converting Physical Screen Pixels to WPF Device Independent Pixels (DIPs) and clamping to the target monitor's work area.
    /// </summary>
    public void PositionNearRegion(ScreenRegion region, bool ignoreLock = false)
    {
        if (region.IsEmpty || (_isLocked && !ignoreLock) || _isClosed) return;

        Dispatcher.Invoke(() =>
        {
            EnsureShown();

            var (dpiX, dpiY) = GetDpiScale();

            // Determine physical monitor work area containing the selected region
            var centerPt = new OverlayNativeMethods.POINT
            {
                X = region.X + region.Width / 2,
                Y = region.Y + region.Height / 2
            };

            double monLeftDip = 0;
            double monTopDip = 0;
            double monRightDip = SystemParameters.PrimaryScreenWidth;
            double monBottomDip = SystemParameters.WorkArea.Bottom;

            nint hMon = OverlayNativeMethods.MonitorFromPoint(centerPt, OverlayNativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMon != nint.Zero)
            {
                var mi = new OverlayNativeMethods.MONITORINFO
                {
                    cbSize = Marshal.SizeOf<OverlayNativeMethods.MONITORINFO>()
                };
                if (OverlayNativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    monLeftDip = mi.rcWork.Left / dpiX;
                    monTopDip = mi.rcWork.Top / dpiY;
                    monRightDip = mi.rcWork.Right / dpiX;
                    monBottomDip = mi.rcWork.Bottom / dpiY;
                }
            }

            double monWidthDip = Math.Max(420, monRightDip - monLeftDip);

            // Convert region physical coordinates to WPF DIPs
            double regLeftDip = region.X / dpiX;
            double regTopDip = region.Y / dpiY;
            double regWidthDip = region.Width / dpiX;
            double regHeightDip = region.Height / dpiY;

            double desiredW = Math.Max(Width > 0 ? Width : 800, Math.Max(regWidthDip + 80, 640));
            double targetWidth = Math.Clamp(desiredW, 400, monWidthDip - 20);

            double targetLeft = regLeftDip + (regWidthDip - targetWidth) / 2.0;
            targetLeft = Math.Clamp(targetLeft, monLeftDip + 10, Math.Max(monLeftDip + 10, monRightDip - targetWidth - 10));

            double estHeight = ActualHeight > 30 ? ActualHeight : 130;

            // Place 8 DIPs below the selected region
            double targetTop = regTopDip + regHeightDip + 8;
            if (targetTop + estHeight > monBottomDip - 8)
            {
                // If it overflows below screen work area, place 8 DIPs above the region
                targetTop = regTopDip - estHeight - 8;
            }

            targetTop = Math.Clamp(targetTop, monTopDip + 8, Math.Max(monTopDip + 8, monBottomDip - estHeight - 8));

            Width = targetWidth;
            OverlayContainer.MaxWidth = Math.Max(360, targetWidth - 24);
            Left = targetLeft;
            Top = targetTop;
            EnsureTopmost();
        });
    }

    private void EnsureWithinScreenBounds()
    {
        double screenW = SystemParameters.PrimaryScreenWidth;
        double screenH = SystemParameters.WorkArea.Bottom > 0 ? SystemParameters.WorkArea.Bottom : SystemParameters.PrimaryScreenHeight;
        if (screenW <= 0 || screenH <= 0) return;

        double w = Width > 0 && !double.IsNaN(Width) ? Width : 900;
        if (w > screenW - 20)
        {
            w = Math.Max(400, screenW - 20);
            Width = w;
            OverlayContainer.MaxWidth = Math.Max(360, w - 24);
        }

        double estH = ActualHeight > 30 ? ActualHeight : 130;

        if (double.IsNaN(Left) || Left < SystemParameters.VirtualScreenLeft || Left + w > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
        {
            Left = Math.Max(10, (screenW - w) / 2.0);
        }

        if (double.IsNaN(Top) || Top < SystemParameters.VirtualScreenTop || Top + estH > screenH - 10)
        {
            Top = Math.Max(10, screenH - estH - 40);
        }
    }

    private (double DpiScaleX, double DpiScaleY) GetDpiScale()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
            return (sx, sy);
        }
        catch
        {
            return (1.0, 1.0);
        }
    }

    /// <summary>
    /// Clears any lingering subtitles from a previous session and resets display state.
    /// </summary>
    public void ClearSubtitles()
    {
        _currentItem = SubtitleItem.Empty;
        _isShowingTestSubtitle = false;
        if (_isClosed) return;

        Dispatcher.Invoke(() =>
        {
            OriginalTextBlock.Text = string.Empty;
            EnglishTextBlock.Text = string.Empty;
            VietnameseTextBlock.Text = string.Empty;
            if (!_isEditMode)
            {
                OverlayContainer.Visibility = Visibility.Collapsed;
            }
        });
    }
}
