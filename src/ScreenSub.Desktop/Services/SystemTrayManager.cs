using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ScreenSub.Core.Enums;
using Path = System.IO.Path;
using WpfPath = System.Windows.Shapes.Path;

namespace ScreenSub.Desktop.Services;

/// <summary>
/// Professional Windows System Tray Manager for ScreenSub AI.
/// Handles notification area icon, right-click Fluent Dark Mode context menu,
/// multi-monitor & Per-Monitor DPI (100%–200%) work-area clamping, left-click restore,
/// balloon notifications, and background operation.
/// </summary>
public sealed class SystemTrayManager : IDisposable
{
    private const int WM_NULL = 0x0000;
    private const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 100;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int NIIF_INFO = 0x00000001;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint LoadImage(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint LoadIcon(nint hInstance, nint lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint Msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// Subclassed ContextMenu that allows non-MenuItem visual headers (Border) to exist
    /// directly in the items panel without being wrapped inside a disabled MenuItem container.
    /// </summary>
    private sealed class FluentTrayContextMenu : ContextMenu
    {
        protected override bool IsItemItsOwnContainerOverride(object item)
            => item is MenuItem or Separator or Border;

        protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
        {
            if (element is MenuItem or Separator)
            {
                base.PrepareContainerForItemOverride(element, item);
            }
        }
    }

    private static ResourceDictionary? _cachedThemeResources;

    private readonly nint _hwnd;
    private readonly HwndSource? _source;
    private nint _hIcon = nint.Zero;
    private bool _isIconAdded;
    private bool _isDisposed;

    private FluentTrayContextMenu? _trayContextMenu;
    private Border? _statusBadgeBorder;
    private TextBlock? _statusBadgeText;
    private MenuItem? _openDashboardMenuItem;
    private MenuItem? _selectRegionMenuItem;
    private MenuItem? _startPauseMenuItem;
    private MenuItem? _stopMenuItem;
    private MenuItem? _settingsMenuItem;
    private MenuItem? _exitMenuItem;

    public ContextMenu? ContextMenu => _trayContextMenu;
    public SystemState CurrentState { get; private set; } = SystemState.Idle;

    public Action? OpenDashboardAction { get; set; }
    public Action? SelectNewRegionAction { get; set; }
    public Action? ToggleStartPauseAction { get; set; }
    public Action? StopAction { get; set; }
    public Action? OpenSettingsAction { get; set; }
    public Action? ExitApplicationAction { get; set; }

    public SystemTrayManager(nint hwnd)
    {
        _hwnd = hwnd;
        if (hwnd != nint.Zero)
        {
            _source = HwndSource.FromHwnd(hwnd);
            _source?.AddHook(HwndHook);
            InitializeIcon();
        }

        BuildContextMenu();
    }

    private void InitializeIcon()
    {
        string baseDir = AppContext.BaseDirectory;
        string icoPath = Path.Combine(baseDir, "Assets", "app.ico");
        if (!File.Exists(icoPath))
        {
            icoPath = Path.Combine(baseDir, "app.ico");
        }

        if (File.Exists(icoPath))
        {
            _hIcon = LoadImage(nint.Zero, icoPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        }

        if (_hIcon == nint.Zero)
        {
            _hIcon = LoadIcon(nint.Zero, (nint)32512); // IDI_APPLICATION
        }

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "ScreenSub AI — Real-time Subtitle Translator"
        };

        _isIconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private static Style? ResolveThemeStyle(string key)
    {
        if (Application.Current?.TryFindResource(key) is Style appStyle)
        {
            return appStyle;
        }

        try
        {
            _cachedThemeResources ??= new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/ScreenSub.Desktop;component/Themes/Theme.xaml", UriKind.Absolute)
            };
            return _cachedThemeResources[key] as Style;
        }
        catch
        {
            return null;
        }
    }

    private void BuildContextMenu()
    {
        var contextMenuStyle = ResolveThemeStyle("TrayContextMenuStyle");
        var menuItemStyle = ResolveThemeStyle("TrayMenuItemStyle");
        var separatorStyle = ResolveThemeStyle("TraySeparatorStyle");

        var menu = new FluentTrayContextMenu
        {
            Background = CreateBrush(15, 23, 42),       // #0F172A
            BorderBrush = CreateBrush(51, 65, 85),      // #334155
            BorderThickness = new Thickness(1),
            Foreground = CreateBrush(248, 250, 252),    // #F8FAFC
            FontSize = 13.5,
            Padding = new Thickness(10, 12, 10, 10),
            MinWidth = 292,
            StaysOpen = false
        };

        if (contextMenuStyle != null)
        {
            menu.Style = contextMenuStyle;
        }

        // 0. Non-interactive Header Card (NOT a disabled MenuItem)
        var headerCard = BuildHeaderCard();
        menu.Items.Add(headerCard);
        menu.Items.Add(CreateSeparator(separatorStyle));

        // 1. Mở Dashboard
        _openDashboardMenuItem = CreateMenuItem(
            header: "Mở Dashboard",
            icon: CreateDashboardIcon(),
            style: menuItemStyle,
            onClick: () => OpenDashboardAction?.Invoke());
        menu.Items.Add(_openDashboardMenuItem);

        // 2. Chọn vùng dịch mới — Ctrl+Alt+Q (right-aligned shortcut)
        _selectRegionMenuItem = CreateMenuItem(
            header: "Chọn vùng dịch mới",
            icon: CreateSelectRegionIcon(),
            style: menuItemStyle,
            gestureText: "Ctrl+Alt+Q",
            onClick: () => SelectNewRegionAction?.Invoke());
        menu.Items.Add(_selectRegionMenuItem);

        menu.Items.Add(CreateSeparator(separatorStyle));

        // 3. Bắt đầu dịch / Tạm dừng / Tiếp tục dịch
        _startPauseMenuItem = CreateMenuItem(
            header: "Bắt đầu dịch",
            icon: CreatePlayCircleIcon(),
            style: menuItemStyle,
            fontWeight: FontWeights.SemiBold,
            tag: "PrimaryAction",
            onClick: () => ToggleStartPauseAction?.Invoke());
        menu.Items.Add(_startPauseMenuItem);

        // 4. Dừng dịch
        _stopMenuItem = CreateMenuItem(
            header: "Dừng dịch",
            icon: CreateStopSquareIcon(),
            style: menuItemStyle,
            isEnabled: false,
            onClick: () => StopAction?.Invoke());
        menu.Items.Add(_stopMenuItem);

        menu.Items.Add(CreateSeparator(separatorStyle));

        // 5. Cài đặt
        _settingsMenuItem = CreateMenuItem(
            header: "Cài đặt",
            icon: CreateSettingsGearIcon(),
            style: menuItemStyle,
            onClick: () => OpenSettingsAction?.Invoke());
        menu.Items.Add(_settingsMenuItem);

        // 6. Thoát ứng dụng
        _exitMenuItem = CreateMenuItem(
            header: "Thoát ứng dụng",
            icon: CreateExitIcon(),
            style: menuItemStyle,
            tag: "DangerAction",
            foreground: CreateBrush(248, 113, 113), // #F87171
            onClick: () => ExitApplicationAction?.Invoke());
        menu.Items.Add(_exitMenuItem);

        menu.Opened += OnContextMenuOpened;
        menu.Closed += OnContextMenuClosed;
        menu.PreviewKeyDown += OnContextMenuPreviewKeyDown;

        _trayContextMenu = menu;
        ApplyStateVisuals(SystemState.Idle);
    }

    private Border BuildHeaderCard()
    {
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left Brand Icon Badge
        var brandBadge = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(9),
            Background = CreateBrush(19, 52, 71),       // #133447
            BorderBrush = CreateBrush(30, 78, 104),     // #1E4E68
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = CreateBrandSubtitleIcon()
        };
        Grid.SetColumn(brandBadge, 0);
        headerGrid.Children.Add(brandBadge);

        // Middle Title & Subtitle
        var titleStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center
        };
        titleStack.Children.Add(new TextBlock
        {
            Text = "ScreenSub AI",
            Foreground = CreateBrush(248, 250, 252),    // #F8FAFC
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            LineHeight = 18
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = "Real-time Subtitle Translator",
            Foreground = CreateBrush(148, 163, 184),    // #94A3B8
            FontSize = 11.5,
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(0, 2, 0, 0)
        });
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(titleStack);

        // Right Dynamic Status Pill
        _statusBadgeText = new TextBlock
        {
            Text = "Đã dừng",
            Foreground = CreateBrush(248, 250, 252),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _statusBadgeBorder = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(10, 1, 0, 0),
            BorderThickness = new Thickness(1),
            Background = CreateBrush(30, 41, 59),       // #1E293B
            BorderBrush = CreateBrush(51, 65, 85),      // #334155
            VerticalAlignment = VerticalAlignment.Top,
            Child = _statusBadgeText
        };
        Grid.SetColumn(_statusBadgeBorder, 2);
        headerGrid.Children.Add(_statusBadgeBorder);

        var headerContainer = new Border
        {
            Padding = new Thickness(6, 2, 6, 4),
            Focusable = false,
            IsHitTestVisible = false,
            Child = headerGrid
        };
        KeyboardNavigation.SetIsTabStop(headerContainer, false);
        return headerContainer;
    }

    private static Separator CreateSeparator(Style? separatorStyle)
    {
        var sep = new Separator
        {
            Focusable = false,
            IsTabStop = false
        };
        if (separatorStyle != null)
        {
            sep.Style = separatorStyle;
        }
        return sep;
    }

    private static MenuItem CreateMenuItem(
        string header,
        FrameworkElement icon,
        Style? style,
        Action onClick,
        string? gestureText = null,
        FontWeight? fontWeight = null,
        string? tag = null,
        Brush? foreground = null,
        bool isEnabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = icon,
            InputGestureText = gestureText ?? string.Empty,
            FontWeight = fontWeight ?? FontWeights.Medium,
            Tag = tag,
            IsEnabled = isEnabled
        };

        if (style != null)
        {
            item.Style = style;
        }

        if (foreground != null)
        {
            item.Foreground = foreground;
        }

        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// Updates the dynamic header status badge, Start/Pause/Resume text + icon, Stop enabled state, and tray tooltip.
    /// </summary>
    public void UpdateState(SystemState state)
    {
        if (_startPauseMenuItem == null) return;

        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(() => ApplyStateVisuals(state));
        }
        else
        {
            ApplyStateVisuals(state);
        }
    }

    private void ApplyStateVisuals(SystemState state)
    {
        CurrentState = state;
        if (_startPauseMenuItem == null || _stopMenuItem == null || _statusBadgeBorder == null || _statusBadgeText == null)
        {
            return;
        }

        if (state == SystemState.Translating || state == SystemState.Starting)
        {
            _statusBadgeText.Text = "Đang dịch";
            _statusBadgeText.Foreground = CreateBrush(94, 234, 212);        // #5EEAD4
            _statusBadgeBorder.Background = CreateBrush(13, 59, 62);        // #0D3B3E
            _statusBadgeBorder.BorderBrush = CreateBrush(20, 184, 166);     // #14B8A6

            _startPauseMenuItem.Header = "Tạm dừng";
            _startPauseMenuItem.Icon = CreatePauseCircleIcon();
            _stopMenuItem.IsEnabled = true;

            UpdateTooltip("ScreenSub AI — Đang dịch trực tiếp (Translating)");
        }
        else if (state == SystemState.Paused)
        {
            _statusBadgeText.Text = "Tạm dừng";
            _statusBadgeText.Foreground = CreateBrush(252, 211, 77);        // #FCD34D
            _statusBadgeBorder.Background = CreateBrush(59, 40, 16);        // #3B2810
            _statusBadgeBorder.BorderBrush = CreateBrush(245, 158, 11);     // #F59E0B

            _startPauseMenuItem.Header = "Tiếp tục dịch";
            _startPauseMenuItem.Icon = CreatePlayCircleIcon();
            _stopMenuItem.IsEnabled = true;

            UpdateTooltip("ScreenSub AI — Tạm dừng (Paused)");
        }
        else
        {
            _statusBadgeText.Text = "Đã dừng";
            _statusBadgeText.Foreground = CreateBrush(248, 250, 252);       // #F8FAFC
            _statusBadgeBorder.Background = CreateBrush(30, 41, 59);        // #1E293B
            _statusBadgeBorder.BorderBrush = CreateBrush(51, 65, 85);       // #334155

            _startPauseMenuItem.Header = "Bắt đầu dịch";
            _startPauseMenuItem.Icon = CreatePlayCircleIcon();
            _stopMenuItem.IsEnabled = false;

            UpdateTooltip("ScreenSub AI — Đã dừng (Idle / Stopped)");
        }
    }

    public void UpdateHotkeyGestureText(string? hotkeyText)
    {
        if (_selectRegionMenuItem == null || string.IsNullOrWhiteSpace(hotkeyText)) return;
        _selectRegionMenuItem.InputGestureText = hotkeyText.Replace(" + ", "+", StringComparison.Ordinal).Trim();
    }

    public void UpdateTooltip(string tip)
    {
        if (!_isIconAdded || _hwnd == nint.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_TIP,
            szTip = tip.Length > 120 ? tip[..120] : tip
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    public void ShowNotification(string title, string message)
    {
        if (!_isIconAdded || _hwnd == nint.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_INFO,
            szInfoTitle = title.Length > 60 ? title[..60] : title,
            szInfo = message.Length > 250 ? message[..250] : message,
            dwInfoFlags = NIIF_INFO,
            uVersionOrTimeout = 3000
        };

        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Opens the Fluent Dark context menu near the current mouse cursor, clamped strictly inside
    /// the active monitor's working area across DPI 100%–200% and multi-monitor layouts.
    /// </summary>
    public void ShowContextMenu()
    {
        if (_trayContextMenu == null) return;

        if (!GetCursorPos(out POINT cursorPx))
        {
            cursorPx = new POINT { X = 1200, Y = 700 };
        }

        ShowContextMenuAtScreenPixel(cursorPx);
    }

    public void ShowContextMenuAtScreenPixel(POINT cursorPx)
    {
        if (_trayContextMenu == null) return;

        nint hMonitor = MonitorFromPoint(cursorPx, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        RECT workAreaPx = GetMonitorInfo(hMonitor, ref mi)
            ? mi.rcWork
            : new RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };

        (double dpiScaleX, double dpiScaleY) = GetMonitorDpiScale(hMonitor);

        _trayContextMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size menuSizeDip = _trayContextMenu.DesiredSize;
        if (menuSizeDip.Width < 280 || menuSizeDip.Height < 240)
        {
            menuSizeDip = new Size(Math.Max(316, menuSizeDip.Width), Math.Max(336, menuSizeDip.Height));
        }

        Point clampedDip = CalculateClampedMenuPositionDip(
            cursorPx,
            workAreaPx,
            dpiScaleX,
            dpiScaleY,
            menuSizeDip);

        _trayContextMenu.PlacementTarget = _source?.RootVisual as UIElement ?? Application.Current?.MainWindow;
        _trayContextMenu.Placement = PlacementMode.AbsolutePoint;
        _trayContextMenu.HorizontalOffset = clampedDip.X;
        _trayContextMenu.VerticalOffset = clampedDip.Y;

        if (_hwnd != nint.Zero)
        {
            SetForegroundWindow(_hwnd);
        }

        _trayContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Pure DPI-aware and multi-monitor-aware popup placement calculator.
    /// Converts physical pixel cursor & monitor work area into WPF DIPs (1/96 inch),
    /// flips above/left when near screen or taskbar edges, and clamps within the monitor's work area.
    /// </summary>
    public static Point CalculateClampedMenuPositionDip(
        POINT cursorPx,
        RECT workAreaPx,
        double dpiScaleX,
        double dpiScaleY,
        Size menuSizeDip)
    {
        double scaleX = dpiScaleX > 0.1 ? dpiScaleX : 1.0;
        double scaleY = dpiScaleY > 0.1 ? dpiScaleY : 1.0;

        double cursorX = cursorPx.X / scaleX;
        double cursorY = cursorPx.Y / scaleY;

        double workLeft = workAreaPx.Left / scaleX;
        double workTop = workAreaPx.Top / scaleY;
        double workRight = workAreaPx.Right / scaleX;
        double workBottom = workAreaPx.Bottom / scaleY;

        double width = Math.Max(100, menuSizeDip.Width);
        double height = Math.Max(100, menuSizeDip.Height);

        const double edgePadding = 4.0;

        // Prefer opening above the cursor if cursor is in the lower portion of the monitor (e.g. bottom taskbar tray)
        double targetY = (cursorY + height > workBottom - edgePadding)
            ? cursorY - height + 6.0
            : cursorY - 6.0;

        // Prefer opening to the left of the cursor if cursor is near the right edge of the monitor
        double targetX = (cursorX + width > workRight - edgePadding)
            ? cursorX - width + 10.0
            : cursorX - 10.0;

        double minX = workLeft + edgePadding;
        double maxX = Math.Max(minX, workRight - width - edgePadding);
        double minY = workTop + edgePadding;
        double maxY = Math.Max(minY, workBottom - height - edgePadding);

        double clampedX = Math.Clamp(targetX, minX, maxX);
        double clampedY = Math.Clamp(targetY, minY, maxY);

        return new Point(Math.Round(clampedX), Math.Round(clampedY));
    }

    private (double ScaleX, double ScaleY) GetMonitorDpiScale(nint hMonitor)
    {
        if (hMonitor != nint.Zero)
        {
            try
            {
                if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
                {
                    return (dpiX / 96.0, dpiY / 96.0);
                }
            }
            catch
            {
                // Fallback below
            }
        }

        if (_source?.RootVisual != null)
        {
            var dpi = VisualTreeHelper.GetDpi(_source.RootVisual);
            if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
            {
                return (dpi.DpiScaleX, dpi.DpiScaleY);
            }
        }

        if (Application.Current?.MainWindow != null)
        {
            var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
            if (dpi.DpiScaleX > 0 && dpi.DpiScaleY > 0)
            {
                return (dpi.DpiScaleX, dpi.DpiScaleY);
            }
        }

        return (1.0, 1.0);
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (_trayContextMenu == null) return;
        _trayContextMenu.Focus();
        Keyboard.Focus(_trayContextMenu);
    }

    private void OnContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (_hwnd != nint.Zero)
        {
            PostMessage(_hwnd, WM_NULL, nint.Zero, nint.Zero);
        }
    }

    private void OnContextMenuPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _trayContextMenu != null)
        {
            _trayContextMenu.IsOpen = false;
            e.Handled = true;
        }
    }

    private nint HwndHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int action = (int)lParam;
            if (action == WM_LBUTTONUP || action == WM_LBUTTONDBLCLK)
            {
                OpenDashboardAction?.Invoke();
                handled = true;
            }
            else if (action == WM_RBUTTONUP || action == WM_CONTEXTMENU)
            {
                ShowContextMenu();
                handled = true;
            }
        }

        return nint.Zero;
    }

    // =========================================================================
    // Crisp 17x17 Vector Icons (Unified Stroke Geometry, Zero Emoji)
    // =========================================================================

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Canvas CreateIconCanvas(double size, params UIElement[] shapes)
    {
        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        foreach (var shape in shapes)
        {
            canvas.Children.Add(shape);
        }

        return canvas;
    }

    private static WpfPath CreateStrokePath(string geometryData, Brush stroke, double thickness = 1.65)
    {
        return new WpfPath
        {
            Data = Geometry.Parse(geometryData),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
    }

    private static WpfPath CreateFillPath(string geometryData, Brush fill)
    {
        return new WpfPath
        {
            Data = Geometry.Parse(geometryData),
            Fill = fill
        };
    }

    private static FrameworkElement CreateBrandSubtitleIcon()
    {
        var cyan = CreateBrush(34, 211, 238); // #22D3EE
        return CreateIconCanvas(18,
            CreateStrokePath("M 2.2,4.2 C 2.2,3.1 3.1,2.2 4.2,2.2 L 13.8,2.2 C 14.9,2.2 15.8,3.1 15.8,4.2 L 15.8,13.2 C 15.8,14.3 14.9,15.2 13.8,15.2 L 4.2,15.2 C 3.1,15.2 2.2,14.3 2.2,13.2 Z", cyan, 1.6),
            CreateStrokePath("M 5.0,9.6 L 9.0,9.6 M 11.2,9.6 L 13.0,9.6 M 5.0,12.2 L 11.6,12.2", cyan, 1.6));
    }

    private static FrameworkElement CreateDashboardIcon()
    {
        var slate = CreateBrush(203, 213, 225); // #CBD5E1
        return CreateIconCanvas(17,
            CreateStrokePath("M 2.2,3.0 C 2.2,2.5 2.5,2.2 3.0,2.2 L 6.5,2.2 C 7.0,2.2 7.3,2.5 7.3,3.0 L 7.3,6.5 C 7.3,7.0 7.0,7.3 6.5,7.3 L 3.0,7.3 C 2.5,7.3 2.2,7.0 2.2,6.5 Z", slate, 1.5),
            CreateStrokePath("M 12.2,1.8 L 15.2,4.8 L 12.2,7.8 L 9.2,4.8 Z", slate, 1.5),
            CreateStrokePath("M 2.2,10.5 C 2.2,10.0 2.5,9.7 3.0,9.7 L 6.5,9.7 C 7.0,9.7 7.3,10.0 7.3,10.5 L 7.3,14.0 C 7.3,14.5 7.0,14.8 6.5,14.8 L 3.0,14.8 C 2.5,14.8 2.2,14.5 2.2,14.0 Z", slate, 1.5),
            CreateStrokePath("M 9.7,10.5 C 9.7,10.0 10.0,9.7 10.5,9.7 L 14.0,9.7 C 14.5,9.7 14.8,10.0 14.8,10.5 L 14.8,14.0 C 14.8,14.5 14.5,14.8 14.0,14.8 L 10.5,14.8 C 10.0,14.8 9.7,14.5 9.7,14.0 Z", slate, 1.5));
    }

    private static FrameworkElement CreateSelectRegionIcon()
    {
        var cyan = CreateBrush(34, 211, 238); // #22D3EE
        return CreateIconCanvas(17,
            CreateStrokePath("M 2.3,5.5 L 2.3,3.5 C 2.3,2.8 2.8,2.3 3.5,2.3 L 5.8,2.3", cyan, 1.65),
            CreateStrokePath("M 11.2,2.3 L 13.5,2.3 C 14.2,2.3 14.7,2.8 14.7,3.5 L 14.7,5.5", cyan, 1.65),
            CreateStrokePath("M 2.3,11.5 L 2.3,13.5 C 2.3,14.2 2.8,14.7 3.5,14.7 L 5.8,14.7", cyan, 1.65),
            CreateStrokePath("M 11.2,14.7 L 13.5,14.7 C 14.2,14.7 14.7,14.2 14.7,13.5 L 14.7,11.5", cyan, 1.65),
            CreateStrokePath("M 5.5,8.5 L 11.5,8.5", cyan, 1.65));
    }

    private static FrameworkElement CreatePlayCircleIcon()
    {
        var teal = CreateBrush(45, 212, 191); // #2DD4BF
        var circle = new Ellipse
        {
            Width = 14.6,
            Height = 14.6,
            Stroke = teal,
            StrokeThickness = 1.6
        };
        Canvas.SetLeft(circle, 1.2);
        Canvas.SetTop(circle, 1.2);

        var triangle = CreateFillPath("M 7.0,5.6 L 11.6,8.5 L 7.0,11.4 Z", teal);
        return CreateIconCanvas(17, circle, triangle);
    }

    private static FrameworkElement CreatePauseCircleIcon()
    {
        var cyan = CreateBrush(56, 189, 248); // #38BDF8
        var circle = new Ellipse
        {
            Width = 14.6,
            Height = 14.6,
            Stroke = cyan,
            StrokeThickness = 1.6
        };
        Canvas.SetLeft(circle, 1.2);
        Canvas.SetTop(circle, 1.2);

        var bars = CreateStrokePath("M 6.8,5.8 L 6.8,11.2 M 10.2,5.8 L 10.2,11.2", cyan, 1.75);
        return CreateIconCanvas(17, circle, bars);
    }

    private static FrameworkElement CreateStopSquareIcon()
    {
        var slate = CreateBrush(148, 163, 184); // #94A3B8
        return CreateIconCanvas(17,
            CreateStrokePath("M 4.4,2.6 L 12.6,2.6 C 13.6,2.6 14.4,3.4 14.4,4.4 L 14.4,12.6 C 14.4,13.6 13.6,14.4 12.6,14.4 L 4.4,14.4 C 3.4,14.4 2.6,13.6 2.6,12.6 L 2.6,4.4 C 2.6,3.4 3.4,2.6 4.4,2.6 Z", slate, 1.6));
    }

    private static FrameworkElement CreateSettingsGearIcon()
    {
        var slate = CreateBrush(148, 163, 184); // #94A3B8
        var innerCircle = new Ellipse
        {
            Width = 5.0,
            Height = 5.0,
            Stroke = slate,
            StrokeThickness = 1.5
        };
        Canvas.SetLeft(innerCircle, 6.0);
        Canvas.SetTop(innerCircle, 6.0);

        var gearRim = CreateStrokePath(
            "M 7.3,1.8 L 9.7,1.8 L 10.1,3.4 L 11.4,4.0 L 12.9,3.1 L 14.5,4.7 L 13.6,6.2 L 14.2,7.5 L 15.8,7.9 L 15.8,10.1 L 14.2,10.5 L 13.6,11.8 L 14.5,13.3 L 12.9,14.9 L 11.4,14.0 L 10.1,14.6 L 9.7,16.2 L 7.3,16.2 L 6.9,14.6 L 5.6,14.0 L 4.1,14.9 L 2.5,13.3 L 3.4,11.8 L 2.8,10.5 L 1.2,10.1 L 1.2,7.9 L 2.8,7.5 L 3.4,6.2 L 2.5,4.7 L 4.1,3.1 L 5.6,4.0 L 6.9,3.4 Z",
            slate,
            1.35);

        return CreateIconCanvas(17, gearRim, innerCircle);
    }

    private static FrameworkElement CreateExitIcon()
    {
        var red = CreateBrush(248, 113, 113); // #F87171
        return CreateIconCanvas(17,
            CreateStrokePath("M 7.2,2.6 L 4.4,2.6 C 3.4,2.6 2.6,3.4 2.6,4.4 L 2.6,12.6 C 2.6,13.6 3.4,14.4 4.4,14.4 L 7.2,14.4", red, 1.65),
            CreateStrokePath("M 10.8,5.5 L 14.2,8.5 L 10.8,11.5 M 6.6,8.5 L 13.9,8.5", red, 1.65));
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_trayContextMenu != null)
        {
            _trayContextMenu.IsOpen = false;
            _trayContextMenu.Opened -= OnContextMenuOpened;
            _trayContextMenu.Closed -= OnContextMenuClosed;
            _trayContextMenu.PreviewKeyDown -= OnContextMenuPreviewKeyDown;
        }

        _source?.RemoveHook(HwndHook);

        if (_isIconAdded)
        {
            var nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIcon(NIM_DELETE, ref nid);
            _isIconAdded = false;
        }

        if (_hIcon != nint.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = nint.Zero;
        }
    }
}
