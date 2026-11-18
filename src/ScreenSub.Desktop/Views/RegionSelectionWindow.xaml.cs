using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenSub.Capture;
using ScreenSub.Core.Models;

namespace ScreenSub.Desktop.Views;

public partial class RegionSelectionWindow : Window
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int VK_ESCAPE = 0x1B;

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(nint hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private Point _startDipPoint;
    private Win32Point _startPhysPoint;
    private bool _isSelecting;
    private HwndSource? _hwndSource;

    public ScreenRegion SelectedRegion { get; private set; } = ScreenRegion.Empty;
    public bool IsCancelled { get; private set; } = true;

    public RegionSelectionWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != nint.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_KEYDOWN && wParam == (nint)VK_ESCAPE)
        {
            CancelAndClose();
            handled = true;
        }
        else if (msg == WM_RBUTTONDOWN)
        {
            CancelAndClose();
            handled = true;
        }
        return nint.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Exclude region selection overlay from screen capture to prevent feedback loops
        try
        {
            if (hwnd != nint.Zero)
            {
                SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { }

        // Position window across all connected monitors accurately in DIPs
        // using physical virtual screen metrics divided by window DPI scale
        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);

        Left = vx / dpiX;
        Top = vy / dpiY;
        Width = vw / dpiX;
        Height = vh / dpiY;

        Activate();
        Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelAndClose();
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.RightButton == MouseButtonState.Pressed)
        {
            CancelAndClose();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _startDipPoint = e.GetPosition(SelectionCanvas);
            GetCursorPos(out _startPhysPoint);
            _isSelecting = true;

            SelectionBox.Visibility = Visibility.Visible;
            DimensionBadge.Visibility = Visibility.Visible;

            Canvas.SetLeft(SelectionBox, _startDipPoint.X);
            Canvas.SetTop(SelectionBox, _startDipPoint.Y);
            SelectionBox.Width = 0;
            SelectionBox.Height = 0;

            CaptureMouse();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting) return;

        var currentDip = e.GetPosition(SelectionCanvas);
        GetCursorPos(out var currentPhys);

        double x = Math.Min(_startDipPoint.X, currentDip.X);
        double y = Math.Min(_startDipPoint.Y, currentDip.Y);
        double width = Math.Abs(currentDip.X - _startDipPoint.X);
        double height = Math.Abs(currentDip.Y - _startDipPoint.Y);

        Canvas.SetLeft(SelectionBox, x);
        Canvas.SetTop(SelectionBox, y);
        SelectionBox.Width = width;
        SelectionBox.Height = height;

        // Position dimension badge just above the selection box or inside
        double badgeX = x;
        double badgeY = y > 35 ? y - 35 : y + height + 8;
        Canvas.SetLeft(DimensionBadge, badgeX);
        Canvas.SetTop(DimensionBadge, badgeY);

        // Display exact physical screen pixels using Win32 cursor difference
        int physW = Math.Abs(currentPhys.X - _startPhysPoint.X);
        int physH = Math.Abs(currentPhys.Y - _startPhysPoint.Y);
        DimensionText.Text = $"{physW} × {physH} px";
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting) return;

        _isSelecting = false;
        ReleaseMouseCapture();

        GetCursorPos(out var endPhysPoint);

        int physX = Math.Min(_startPhysPoint.X, endPhysPoint.X);
        int physY = Math.Min(_startPhysPoint.Y, endPhysPoint.Y);
        int physW = Math.Abs(endPhysPoint.X - _startPhysPoint.X);
        int physH = Math.Abs(endPhysPoint.Y - _startPhysPoint.Y);

        // Require minimum selection to avoid accidental single clicks
        if (physW > 20 && physH > 10)
        {
            SelectedRegion = new ScreenRegion(physX, physY, physW, physH);
            IsCancelled = false;
        }
        else
        {
            IsCancelled = true;
        }

        HideImmediatelyBeforeClose();
        Close();
    }

    private void CancelAndClose()
    {
        IsCancelled = true;
        HideImmediatelyBeforeClose();
        Close();
    }

    private void HideImmediatelyBeforeClose()
    {
        SelectionBox.Visibility = Visibility.Collapsed;
        DimensionBadge.Visibility = Visibility.Collapsed;
        Opacity = 0;
        Visibility = Visibility.Hidden;
    }
}
