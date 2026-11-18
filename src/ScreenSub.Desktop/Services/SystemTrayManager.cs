using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenSub.Core.Enums;

namespace ScreenSub.Desktop.Services;

/// <summary>
/// Professional Windows System Tray Manager for ScreenSub AI.
/// Handles notification area icon, right-click context menu (styled with Fluent Dark theme),
/// left-click restore, balloon notifications, and background operation.
/// </summary>
public sealed class SystemTrayManager : IDisposable
{
    private const int WM_USER = 0x0400;
    public const int WM_TRAYICON = WM_USER + 100;

    private const int NIM_ADD = 0x00000000;
    private const int NIM_MODIFY = 0x00000001;
    private const int NIM_DELETE = 0x00000002;
    private const int NIM_SETVERSION = 0x00000004;

    private const int NIF_MESSAGE = 0x00000001;
    private const int NIF_ICON = 0x00000002;
    private const int NIF_TIP = 0x00000004;
    private const int NIF_INFO = 0x00000010;

    private const int NIIF_INFO = 0x00000001;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

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

    private readonly nint _hwnd;
    private readonly HwndSource? _source;
    private nint _hIcon = nint.Zero;
    private bool _isIconAdded;
    private bool _isDisposed;

    private ContextMenu? _trayContextMenu;
    private MenuItem? _startPauseMenuItem;

    public Action? OpenDashboardAction { get; set; }
    public Action? SelectNewRegionAction { get; set; }
    public Action? ToggleStartPauseAction { get; set; }
    public Action? StopAction { get; set; }
    public Action? OpenSettingsAction { get; set; }
    public Action? ExitApplicationAction { get; set; }

    public SystemTrayManager(nint hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(HwndHook);

        InitializeIcon();
        BuildContextMenu();
    }

    private void InitializeIcon()
    {
        // Try load custom app.ico
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

        // Fallback to standard application icon if custom icon fails
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
            szTip = "ScreenSub AI — Realtime Subtitle Translator"
        };

        _isIconAdded = Shell_NotifyIcon(NIM_ADD, ref nid);
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(15, 23, 42)),      // #0F172A
            BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)),     // #334155
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)),   // #F8FAFC
            FontSize = 13,
            Padding = new Thickness(4)
        };

        // Header / App Title Item (disabled header)
        var titleItem = new MenuItem
        {
            Header = "ScreenSub AI Beta",
            IsEnabled = false,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(56, 189, 248))      // #38BDF8
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new Separator());

        // 1. Mở Dashboard
        var openDashItem = new MenuItem
        {
            Header = "🖥  Mở Dashboard",
            FontWeight = FontWeights.SemiBold
        };
        openDashItem.Click += (_, _) => OpenDashboardAction?.Invoke();
        menu.Items.Add(openDashItem);

        // 2. Chọn vùng dịch mới (Ctrl + Alt + Q)
        var selectRegionItem = new MenuItem
        {
            Header = "✂  Chọn vùng dịch mới (Ctrl + Alt + Q)",
            FontWeight = FontWeights.SemiBold
        };
        selectRegionItem.Click += (_, _) => SelectNewRegionAction?.Invoke();
        menu.Items.Add(selectRegionItem);

        menu.Items.Add(new Separator());

        // 3. Bắt đầu / Tạm dừng dịch
        _startPauseMenuItem = new MenuItem
        {
            Header = "▶  Bắt đầu dịch",
            FontWeight = FontWeights.SemiBold
        };
        _startPauseMenuItem.Click += (_, _) => ToggleStartPauseAction?.Invoke();
        menu.Items.Add(_startPauseMenuItem);

        // 4. Dừng dịch
        var stopItem = new MenuItem
        {
            Header = "⏹  Dừng dịch"
        };
        stopItem.Click += (_, _) => StopAction?.Invoke();
        menu.Items.Add(stopItem);

        menu.Items.Add(new Separator());

        // 5. Cài đặt
        var settingsItem = new MenuItem
        {
            Header = "⚙  Cài đặt"
        };
        settingsItem.Click += (_, _) => OpenSettingsAction?.Invoke();
        menu.Items.Add(settingsItem);

        // 6. Thoát ứng dụng
        var exitItem = new MenuItem
        {
            Header = "🚪  Thoát ứng dụng",
            Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113))    // #F87171 Red
        };
        exitItem.Click += (_, _) => ExitApplicationAction?.Invoke();
        menu.Items.Add(exitItem);

        _trayContextMenu = menu;
    }

    /// <summary>
    /// Updates the dynamic menu text according to current system state (e.g. Start vs Pause).
    /// </summary>
    public void UpdateState(SystemState state)
    {
        if (_startPauseMenuItem == null) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (state == SystemState.Translating || state == SystemState.Starting)
            {
                _startPauseMenuItem.Header = "⏸  Tạm dừng dịch";
                UpdateTooltip("ScreenSub AI — Đang dịch trực tiếp (Translating)");
            }
            else if (state == SystemState.Paused)
            {
                _startPauseMenuItem.Header = "▶  Tiếp tục dịch";
                UpdateTooltip("ScreenSub AI — Tạm dừng (Paused)");
            }
            else
            {
                _startPauseMenuItem.Header = "▶  Bắt đầu dịch";
                UpdateTooltip("ScreenSub AI — Sẵn sàng (Idle / Stopped)");
            }
        });
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
            else if (action == WM_RBUTTONUP)
            {
                if (_trayContextMenu != null)
                {
                    SetForegroundWindow(_hwnd);
                    _trayContextMenu.IsOpen = true;
                }
                handled = true;
            }
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

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

