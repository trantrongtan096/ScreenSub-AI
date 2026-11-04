using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Overlay;

public partial class SubtitleOverlayWindow : Window
{
    private nint _hwnd;
    private bool _isEditMode;
    private bool _clickThrough = true;
    private bool _isLocked = false;
    private DisplayMode _displayMode = DisplayMode.Triple;

    public bool IsEditMode => _isEditMode;

    public SubtitleOverlayWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Exclude this overlay window from screen capture to prevent infinite OCR loop
        try
        {
            OverlayNativeMethods.SetWindowDisplayAffinity(_hwnd, OverlayNativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }
        catch { }

        ApplyClickThrough();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && _isEditMode && !_isLocked)
        {
            DragMove();
        }
    }

    public void SetEditMode(bool isEdit)
    {
        _isEditMode = isEdit;
        EditModeBanner.Visibility = isEdit ? Visibility.Visible : Visibility.Collapsed;
        ApplyClickThrough();
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

        int exStyle = OverlayNativeMethods.GetWindowLong(_hwnd, OverlayNativeMethods.GWL_EXSTYLE);

        // In edit mode, click-through MUST be disabled so user can drag window
        if (_isEditMode || !_clickThrough)
        {
            exStyle &= ~OverlayNativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            exStyle |= OverlayNativeMethods.WS_EX_TRANSPARENT;
        }

        exStyle |= OverlayNativeMethods.WS_EX_LAYERED;
        OverlayNativeMethods.SetWindowLong(_hwnd, OverlayNativeMethods.GWL_EXSTYLE, exStyle);
    }

    public void UpdateSubtitle(SubtitleItem item)
    {
        Dispatcher.Invoke(() =>
        {
            if (!item.HasContent)
            {
                OriginalTextBlock.Text = string.Empty;
                EnglishTextBlock.Text = string.Empty;
                VietnameseTextBlock.Text = string.Empty;
                return;
            }

            OriginalTextBlock.Text = item.ChineseText.Length > 0 ? item.ChineseText : item.OriginalText;
            EnglishTextBlock.Text = item.EnglishText;
            VietnameseTextBlock.Text = item.VietnameseText;

            ApplyDisplayMode();
        });
    }

    public void UpdateDisplayMode(DisplayMode mode)
    {
        _displayMode = mode;
        Dispatcher.Invoke(ApplyDisplayMode);
    }

    private void ApplyDisplayMode()
    {
        switch (_displayMode)
        {
            case DisplayMode.Single:
                OriginalTextBlock.Visibility = Visibility.Collapsed;
                EnglishTextBlock.Visibility = Visibility.Collapsed;
                VietnameseTextBlock.Visibility = Visibility.Visible;
                break;

            case DisplayMode.Dual:
                OriginalTextBlock.Visibility = Visibility.Visible;
                EnglishTextBlock.Visibility = Visibility.Collapsed;
                VietnameseTextBlock.Visibility = Visibility.Visible;
                break;

            case DisplayMode.Triple:
            default:
                OriginalTextBlock.Visibility = Visibility.Visible;
                EnglishTextBlock.Visibility = Visibility.Visible;
                VietnameseTextBlock.Visibility = Visibility.Visible;
                break;
        }
    }

    public void UpdateStyling(double fontSize, string textColorHex, double backgroundOpacity)
    {
        Dispatcher.Invoke(() =>
        {
            // Update Font size
            OriginalTextBlock.FontSize = Math.Max(14, fontSize);
            EnglishTextBlock.FontSize = Math.Max(12, fontSize * 0.85);
            VietnameseTextBlock.FontSize = Math.Max(12, fontSize * 0.85);

            // Update Text Color
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(textColorHex);
                var brush = new SolidColorBrush(color);
                VietnameseTextBlock.Foreground = brush;
                OriginalTextBlock.Foreground = brush;
            }
            catch { }

            // Update Background Opacity
            byte alpha = (byte)Math.Clamp((int)(backgroundOpacity * 255), 0, 255);
            OverlayContainer.Background = new SolidColorBrush(Color.FromArgb(alpha, 13, 17, 26));
        });
    }
}
