using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenSub.Desktop.Services;
using ScreenSub.Desktop.ViewModels;

namespace ScreenSub.Desktop;

public partial class MainWindow : Window
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private GlobalHotkeyManager? _hotkeyManager;
    private SystemTrayManager? _trayManager;
    private readonly MainViewModel _viewModel;
    private int _registeredHotkeyId = -1;
    private ModifierKeys _currentModifiers = ModifierKeys.Control | ModifierKeys.Alt;
    private Key _currentKey = Key.Q;
    private bool _isExiting = false;
    private bool _hasShownTrayBalloon = false;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        if (Application.Current == null)
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/ScreenSub.Desktop;component/Themes/Theme.xaml", UriKind.Absolute)
            });
        }
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        StateChanged += OnWindowStateChanged;
        SizeChanged += (_, _) => UpdateMaximizedLayoutBounds();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);
        UpdateMaximizedLayoutBounds();
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return nint.Zero;
    }

    private void WmGetMinMaxInfo(nint hwnd, nint lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != nint.Zero)
        {
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                RECT rcWork = monitorInfo.rcWork;
                RECT rcMonitor = monitorInfo.rcMonitor;

                mmi.ptMaxPosition.X = Math.Abs(rcWork.Left - rcMonitor.Left);
                mmi.ptMaxPosition.Y = Math.Abs(rcWork.Top - rcMonitor.Top);
                mmi.ptMaxSize.X = Math.Abs(rcWork.Right - rcWork.Left);
                mmi.ptMaxSize.Y = Math.Abs(rcWork.Bottom - rcWork.Top);
                mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, mmi.ptMaxSize.X);
                mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, mmi.ptMaxSize.Y);

                var dpi = VisualTreeHelper.GetDpi(this);
                double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
                mmi.ptMinTrackSize.X = Math.Min((int)Math.Round(MinWidth * scaleX), mmi.ptMaxSize.X);
                mmi.ptMinTrackSize.Y = Math.Min((int)Math.Round(MinHeight * scaleY), mmi.ptMaxSize.Y);
            }
        }
        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButtonGlyph != null)
        {
            MaximizeButtonGlyph.Text = WindowState == WindowState.Maximized ? "❐" : "▢";
        }
        UpdateMaximizedLayoutBounds();
    }

    private void UpdateMaximizedLayoutBounds()
    {
        if (RootLayoutGrid == null) return;

        if (WindowState != WindowState.Maximized)
        {
            RootLayoutGrid.Margin = new Thickness(0);
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero) return;

        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == nint.Zero) return;

        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo) || !GetWindowRect(hwnd, out RECT rcWindow))
        {
            return;
        }

        // Compensate exact pixel overhang (if any) when WindowChrome maximizes beyond rcWork on any DPI (100%–200%)
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        RECT rcWork = monitorInfo.rcWork;
        double leftPad = Math.Max(0, rcWork.Left - rcWindow.Left) / scaleX;
        double topPad = Math.Max(0, rcWork.Top - rcWindow.Top) / scaleY;
        double rightPad = Math.Max(0, rcWindow.Right - rcWork.Right) / scaleX;
        double bottomPad = Math.Max(0, rcWindow.Bottom - rcWork.Bottom) / scaleY;

        RootLayoutGrid.Margin = new Thickness(leftPad, topPad, rightPad, bottomPad);
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hotkeyManager = new GlobalHotkeyManager(hwnd);
        _trayManager = new SystemTrayManager(hwnd)
        {
            OpenDashboardAction = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    if (WindowState == WindowState.Minimized)
                    {
                        WindowState = WindowState.Normal;
                    }
                    Activate();
                    _viewModel.Navigate("Dashboard");
                });
            },
            SelectNewRegionAction = () =>
            {
                Dispatcher.Invoke(TriggerSelectRegion);
            },
            ToggleStartPauseAction = () =>
            {
                Dispatcher.Invoke(() => _viewModel.ToggleStartPauseCommand.Execute(null));
            },
            StopAction = () =>
            {
                Dispatcher.Invoke(() => _viewModel.StopTranslationCommand.Execute(null));
            },
            OpenSettingsAction = () =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    if (WindowState == WindowState.Minimized)
                    {
                        WindowState = WindowState.Normal;
                    }
                    Activate();
                    _viewModel.Navigate("Cài đặt");
                });
            },
            ExitApplicationAction = () =>
            {
                Dispatcher.Invoke(ShutdownApplication);
            }
        };

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.State))
            {
                _trayManager?.UpdateState(_viewModel.State);
            }
        };

        // Load configured hotkey (default: Ctrl + Alt + Q)
        var savedMods = _viewModel.CurrentHotkeyModifiers;
        var savedKey = _viewModel.CurrentHotkeyKey;

        _currentModifiers = savedMods != ModifierKeys.None ? savedMods : (ModifierKeys.Control | ModifierKeys.Alt);
        _currentKey = savedKey != Key.None ? savedKey : Key.Q;

        // Register Global Hotkey
        bool registered = _hotkeyManager.TryRegister(_currentModifiers, _currentKey, TriggerSelectRegion, out _registeredHotkeyId);
        if (!registered)
        {
            _viewModel.HotkeyStatusMessage = $"Không thể đăng ký phím tắt '{_viewModel.HotkeyDisplayText}' (có thể bị chiếm dụng bởi ứng dụng khác). Bạn vẫn có thể nhấn nút trên giao diện.";
            _viewModel.IsHotkeyError = true;
        }

        // Wire dynamic hotkey change requests
        _viewModel.RequestRegisterHotkey += (mods, key) =>
        {
            if (_hotkeyManager == null) return false;

            _hotkeyManager.UnregisterAll();
            bool ok = _hotkeyManager.TryRegister(mods, key, TriggerSelectRegion, out _registeredHotkeyId);
            if (!ok)
            {
                // Re-register previous active hotkey
                _hotkeyManager.TryRegister(_currentModifiers, _currentKey, TriggerSelectRegion, out _registeredHotkeyId);
                return false;
            }

            _currentModifiers = mods;
            _currentKey = key;
            return true;
        };
    }

    private void TriggerSelectRegion()
    {
        Dispatcher.Invoke(() =>
        {
            if (_viewModel.SelectRegionCommand.CanExecute(null))
            {
                _viewModel.SelectRegionCommand.Execute(null);
            }
        });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.IsRecordingHotkey)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                _viewModel.CancelRecordHotkey();
                e.Handled = true;
                return;
            }

            var modifiers = Keyboard.Modifiers;
            _viewModel.ApplyRecordedHotkey(modifiers, key);
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting && _viewModel.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            if (!_hasShownTrayBalloon)
            {
                _hasShownTrayBalloon = true;
                _trayManager?.ShowNotification(
                    "ScreenSub AI — Đang chạy nền",
                    $"Dashboard đã thu nhỏ xuống khay hệ thống. Nhấn {_viewModel.HotkeyDisplayText} để chọn vùng và dịch tự động!");
            }
            return;
        }

        ShutdownApplication();
    }

    public void ShutdownApplication()
    {
        if (_isExiting) return;
        _isExiting = true;

        _trayManager?.Dispose();
        _hotkeyManager?.Dispose();
        _viewModel.CloseOverlay();
        Application.Current?.Shutdown();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
