using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using Moq;
using ScreenSub.Audio.Capture;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Desktop;
using ScreenSub.Desktop.Services;
using ScreenSub.Desktop.ViewModels;
using ScreenSub.Overlay;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests;

public class DesktopAndAudioHardwareTests
{
    private readonly ITestOutputHelper _output;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint GR_GDIOBJECTS = 0;
    private const uint GR_USEROBJECTS = 1;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(nint hProcess, uint uiFlags);

    public DesktopAndAudioHardwareTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task LiveDesktop_ScreenCaptureAndWasapiHardwareProbe_ShouldSucceedOnActiveSession()
    {
        // 1. Real desktop VirtualScreen + BitBlt check (succeeds on unlocked desktop; gracefully records Win32 lock/access state if BitBlt handle is isolated)
        var capture = new ScreenCaptureService();
        var bounds = capture.GetVirtualScreenBounds();
        _output.WriteLine($"[DESKTOP_CAPTURE] VirtualBounds=({bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height})");
        bounds.Width.Should().BeGreaterThan(0);
        bounds.Height.Should().BeGreaterThan(0);

        var desktopRegion = new ScreenRegion(bounds.X, bounds.Y, Math.Min(640, bounds.Width), Math.Min(160, bounds.Height));
        var desktopFrame = await capture.CaptureWithDiagnosticsAsync(desktopRegion, null, RegionMode.FixedRegion, CaptureSourceType.FullScreen);
        _output.WriteLine($"[DESKTOP_CAPTURE_BITBLT] Success={desktopFrame.IsSuccess}, Kind={desktopFrame.ErrorKind}, Backend={desktopFrame.BackendName}, Bytes={desktopFrame.ImageBytes?.Length ?? 0}, Latency={desktopFrame.CaptureLatencyMs}ms");

        // 2. Real WPF Window DWM PrintWindow Capture (works regardless of lock-screen / BitBlt session isolation!)
        CaptureFrameResult? winFrame = null;
        Exception? wpfEx = null;
        var sta = new Thread(() =>
        {
            try
            {
                var win = new System.Windows.Window
                {
                    Width = 520,
                    Height = 140,
                    Left = 40,
                    Top = 40,
                    WindowStyle = System.Windows.WindowStyle.None,
                    ShowActivated = false,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 24, 34)),
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = "ScreenSub Live Window Capture Test",
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 24,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center
                    }
                };
                win.Show();
                win.UpdateLayout();
                nint hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                var info = new WindowInfo { Handle = hwnd, Title = "TestTarget", ProcessName = "testhost" };
                winFrame = capture.CaptureWithDiagnosticsAsync(new ScreenRegion(80, 80, 400, 100), info, RegionMode.FixedRegion, CaptureSourceType.Window).GetAwaiter().GetResult();
                win.Close();
            }
            catch (Exception ex)
            {
                wpfEx = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        wpfEx.Should().BeNull();
        _output.WriteLine($"[WINDOW_CAPTURE_DWM] Success={winFrame?.IsSuccess}, Backend={winFrame?.BackendName}, Bytes={winFrame?.ImageBytes?.Length ?? 0}, Latency={winFrame?.CaptureLatencyMs ?? -1}ms, Size={winFrame?.CropWidth}x{winFrame?.CropHeight}");
        winFrame.Should().NotBeNull();
        winFrame!.IsSuccess.Should().BeTrue("PrintWindow DWM capture against a real WPF HWND must succeed");
        winFrame.ImageBytes!.Length.Should().BeGreaterThan(200);

        // 3. Real hardware WASAPI loopback enumeration
        using var wasapi = new WasapiAudioCaptureService();
        var devices = wasapi.GetOutputDevices();
        _output.WriteLine($"[WASAPI_DEVICES] Count={devices.Count}");
        foreach (var d in devices)
        {
            _output.WriteLine($"  Device: Name='{d.Name}', IsDefault={d.IsDefault}");
        }
        devices.Count.Should().BeGreaterThanOrEqualTo(1, "active Windows audio host should expose at least one WASAPI render endpoint");
    }

    [Fact]
    public void OverlayWindow_Win32ClickThroughTopmostFlagsAndGdiUserHandleStability_ShouldHoldOver50Updates()
    {
        Exception? threadEx = null;
        int exStyle = 0;
        uint gdiStart = 0, gdiEnd = 0, userStart = 0, userEnd = 0;
        long p50Ms = 0, p95Ms = 0;

        var staThread = new Thread(() =>
        {
            try
            {
                var proc = Process.GetCurrentProcess().Handle;
                var overlay = new SubtitleOverlayWindow();
                overlay.EnsureShown();
                overlay.PositionNearRegion(new ScreenRegion(200, 500, 800, 140), ignoreLock: true);
                overlay.ShowTestSubtitle();

                nint hwnd = overlay.Handle;
                hwnd.Should().NotBe(nint.Zero);
                exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

                gdiStart = GetGuiResources(proc, GR_GDIOBJECTS);
                userStart = GetGuiResources(proc, GR_USEROBJECTS);

                var lats = new System.Collections.Generic.List<long>(50);
                for (int i = 0; i < 50; i++)
                {
                    var sw = Stopwatch.StartNew();
                    overlay.UpdateSubtitle(new SubtitleItem
                    {
                        OriginalText = $"Subtitle line #{i} — rapid test",
                        OriginalLanguageCode = "en",
                        EnglishText = $"Subtitle line #{i} — rapid test",
                        VietnameseText = $"Dòng phụ đề số #{i} — kiểm tra nhanh",
                        DisplayMode = DisplayMode.Triple
                    });
                    sw.Stop();
                    lats.Add(Math.Max(sw.ElapsedMilliseconds, overlay.LastUpdateLatencyMs));
                }
                lats.Sort();
                p50Ms = lats[lats.Count / 2];
                p95Ms = lats[(int)(0.95 * (lats.Count - 1))];

                gdiEnd = GetGuiResources(proc, GR_GDIOBJECTS);
                userEnd = GetGuiResources(proc, GR_USEROBJECTS);

                overlay.Close();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        threadEx.Should().BeNull();
        _output.WriteLine($"[OVERLAY_WIN32] ExStyle=0x{exStyle:X8} (Topmost={(exStyle & WS_EX_TOPMOST) != 0}, Transparent={(exStyle & WS_EX_TRANSPARENT) != 0}, Tool={(exStyle & WS_EX_TOOLWINDOW) != 0}, NoAct={(exStyle & WS_EX_NOACTIVATE) != 0})");
        _output.WriteLine($"[OVERLAY_RES] GDI={gdiStart}->{gdiEnd} (delta={(long)gdiEnd - gdiStart}), USER={userStart}->{userEnd} (delta={(long)userEnd - userStart}), UpdateP50={p50Ms}ms, UpdateP95={p95Ms}ms");

        (exStyle & WS_EX_TOPMOST).Should().NotBe(0);
        (exStyle & WS_EX_TRANSPARENT).Should().NotBe(0);
        (exStyle & WS_EX_TOOLWINDOW).Should().NotBe(0);
        (exStyle & WS_EX_NOACTIVATE).Should().NotBe(0);
        ((long)gdiEnd - gdiStart).Should().BeLessThanOrEqualTo(10, "50 overlay updates must not leak GDI handles");
        ((long)userEnd - userStart).Should().BeLessThanOrEqualTo(5, "50 overlay updates must not leak USER handles");
    }

    [Fact]
    public void SystemTrayContextMenu_FluentDarkRedesign_ShouldEliminateLeftWhiteGutter_VerifyStatesActionsDpiAndRenderScreenshots()
    {
        Exception? staException = null;
        string artifactDir = Environment.GetEnvironmentVariable("SCREENSUB_TRAY_ARTIFACT_DIR")
                             ?? Path.Combine(Path.GetTempPath(), "ScreenSubAI_TrayScreenshots");
        Directory.CreateDirectory(artifactDir);
        string stoppedPng = Path.Combine(artifactDir, "tray_menu_stopped.png");
        string translatingPng = Path.Combine(artifactDir, "tray_menu_translating.png");
        string pausedPng = Path.Combine(artifactDir, "tray_menu_paused.png");

        var sta = new Thread(() =>
        {
            try
            {
                var hostWin = new Window
                {
                    Width = 200,
                    Height = 120,
                    Left = -2000,
                    Top = -2000,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                hostWin.Show();
                nint hwnd = new System.Windows.Interop.WindowInteropHelper(hostWin).Handle;

                int openDashCount = 0, selectRegionCount = 0, toggleCount = 0, stopCount = 0, settingsCount = 0, exitCount = 0;

                using var tray = new SystemTrayManager(hwnd)
                {
                    OpenDashboardAction = () => openDashCount++,
                    SelectNewRegionAction = () => selectRegionCount++,
                    ToggleStartPauseAction = () => toggleCount++,
                    StopAction = () => stopCount++,
                    OpenSettingsAction = () => settingsCount++,
                    ExitApplicationAction = () => exitCount++
                };

                var menu = tray.ContextMenu;
                menu.Should().NotBeNull();

                // 1. Verify Header is a non-focusable Border (NOT a disabled MenuItem)
                menu!.Items.Count.Should().Be(10); // 1 Header Border + 3 Separators + 6 MenuItems
                var headerBorder = menu.Items[0] as Border;
                headerBorder.Should().NotBeNull("header must be a pure Border container, not a disabled MenuItem");
                headerBorder!.Focusable.Should().BeFalse();
                headerBorder.IsHitTestVisible.Should().BeFalse();

                var menuItems = menu.Items.OfType<MenuItem>().ToList();
                menuItems.Count.Should().Be(6, "only the 6 interactive actions should be MenuItems");

                // 2. Verify labels, shortcut gesture text, and absence of emoji
                menuItems[0].Header.Should().Be("Mở Dashboard");
                menuItems[1].Header.Should().Be("Chọn vùng dịch mới");
                menuItems[1].InputGestureText.Should().Be("Ctrl+Alt+Q");
                menuItems[2].Header.Should().Be("Bắt đầu dịch");
                menuItems[3].Header.Should().Be("Dừng dịch");
                menuItems[3].IsEnabled.Should().BeFalse("Stop must be disabled when Idle/Stopped");
                menuItems[4].Header.Should().Be("Cài đặt");
                menuItems[5].Header.Should().Be("Thoát ứng dụng");

                foreach (var mi in menuItems)
                {
                    mi.Icon.Should().BeOfType<Canvas>("every MenuItem must use a vector Canvas icon instead of emoji");
                    mi.Height.Should().BeInRange(36, 40);
                }

                // 3. Open the real context menu, realize its visual tree, and verify custom ControlTemplate (no white left gutter)
                tray.ShowContextMenuAtScreenPixel(new SystemTrayManager.POINT { X = 1880, Y = 1030 });
                menu.ApplyTemplate();
                menu.UpdateLayout();

                var outerBorder = menu.Template.FindName("MenuOuterBorder", menu) as Border;
                outerBorder.Should().NotBeNull("custom TrayContextMenuStyle ControlTemplate must define MenuOuterBorder");
                outerBorder!.CornerRadius.Should().Be(new CornerRadius(12));
                ((SolidColorBrush)outerBorder.Background).Color.Should().Be(Color.FromRgb(15, 23, 42));   // #0F172A
                ((SolidColorBrush)outerBorder.BorderBrush).Color.Should().Be(Color.FromRgb(51, 65, 85));  // #334155

                foreach (var mi in menuItems)
                {
                    mi.ApplyTemplate();
                    mi.UpdateLayout();
                    var itemBorder = mi.Template.FindName("ItemBorder", mi) as Border;
                    itemBorder.Should().NotBeNull($"MenuItem '{mi.Header}' must use custom TrayMenuItemStyle template without left gutter");
                    itemBorder!.CornerRadius.Should().Be(new CornerRadius(8));
                }

                // Render Screenshot 1: Stopped state (with primary action 'Bắt đầu dịch' highlighted as in mockup)
                var startItemBorder = menuItems[2].Template.FindName("ItemBorder", menuItems[2]) as Border;
                if (startItemBorder != null)
                {
                    startItemBorder.Background = new SolidColorBrush(Color.FromRgb(22, 59, 69));   // #163B45
                    startItemBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(37, 92, 105)); // #255C69
                }
                outerBorder.UpdateLayout();
                if (Directory.Exists(artifactDir))
                {
                    SaveElementPng(outerBorder, stoppedPng);
                }
                if (startItemBorder != null)
                {
                    startItemBorder.ClearValue(Border.BackgroundProperty);
                    startItemBorder.ClearValue(Border.BorderBrushProperty);
                }

                // 4. Verify state transition: Translating ("Đang dịch", "Tạm dừng", Stop enabled)
                tray.UpdateState(SystemState.Translating);
                outerBorder.UpdateLayout();
                menuItems[2].Header.Should().Be("Tạm dừng");
                menuItems[3].IsEnabled.Should().BeTrue("Stop must be enabled while Translating");
                if (Directory.Exists(artifactDir))
                {
                    SaveElementPng(outerBorder, translatingPng);
                }

                // 5. Verify state transition: Paused ("Tạm dừng", "Tiếp tục dịch", Stop enabled)
                tray.UpdateState(SystemState.Paused);
                outerBorder.UpdateLayout();
                menuItems[2].Header.Should().Be("Tiếp tục dịch");
                menuItems[3].IsEnabled.Should().BeTrue("Stop must be enabled while Paused");
                if (Directory.Exists(artifactDir))
                {
                    SaveElementPng(outerBorder, pausedPng);
                }

                // 6. Verify state transition back to Stopped ("Đã dừng", "Bắt đầu dịch", Stop disabled)
                tray.UpdateState(SystemState.Stopped);
                menuItems[2].Header.Should().Be("Bắt đầu dịch");
                menuItems[3].IsEnabled.Should().BeFalse();

                // 7. Verify all 6 actions fire properly
                tray.UpdateState(SystemState.Translating); // enable Stop item so all 6 can be invoked
                menuItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menuItems[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menuItems[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menuItems[3].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menuItems[4].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menuItems[5].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

                openDashCount.Should().Be(1);
                selectRegionCount.Should().Be(1);
                toggleCount.Should().Be(1);
                stopCount.Should().Be(1);
                settingsCount.Should().Be(1);
                exitCount.Should().Be(1);

                menu.IsOpen = false;
                hostWin.Close();
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        staException.Should().BeNull();

        // 8. Verify DPI 100%–200% and multi-monitor edge clamping math
        var menuSize = new Size(312, 344);
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var workPx = new SystemTrayManager.RECT
            {
                Left = 0,
                Top = 0,
                Right = (int)(1920 * scale),
                Bottom = (int)(1032 * scale)
            };
            var trayCursorPx = new SystemTrayManager.POINT
            {
                X = (int)(1910 * scale),
                Y = (int)(1028 * scale)
            };

            Point posDip = SystemTrayManager.CalculateClampedMenuPositionDip(trayCursorPx, workPx, scale, scale, menuSize);
            posDip.X.Should().BeGreaterThanOrEqualTo(4);
            (posDip.X + menuSize.Width).Should().BeLessThanOrEqualTo(1920 - 4);
            posDip.Y.Should().BeGreaterThanOrEqualTo(4);
            (posDip.Y + menuSize.Height).Should().BeLessThanOrEqualTo(1032 - 4);
        }

        // Secondary monitor on the left (negative coordinates) at 150% DPI
        var leftMonWorkPx = new SystemTrayManager.RECT { Left = -2880, Top = 0, Right = 0, Bottom = 1548 };
        var leftMonCursorPx = new SystemTrayManager.POINT { X = -2875, Y = 1540 };
        Point leftPosDip = SystemTrayManager.CalculateClampedMenuPositionDip(leftMonCursorPx, leftMonWorkPx, 1.5, 1.5, menuSize);
        leftPosDip.X.Should().BeGreaterThanOrEqualTo(-1920 + 4);
        (leftPosDip.X + menuSize.Width).Should().BeLessThanOrEqualTo(-4);
        (leftPosDip.Y + menuSize.Height).Should().BeLessThanOrEqualTo(1032 - 4);
    }

    private static void SaveElementPng(FrameworkElement element, string filePath)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = element.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            size = new Size(316, 350);
        }
        element.Arrange(new Rect(0, 0, size.Width, size.Height));
        element.UpdateLayout();

        const double dpi = 192.0; // 2x crisp render
        const double scale = dpi / 96.0;
        int pxW = (int)Math.Ceiling(size.Width * scale);
        int pxH = (int)Math.Ceiling(size.Height * scale);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 12, 22)), null, new Rect(0, 0, size.Width, size.Height));
            var vb = new VisualBrush(element) { Stretch = Stretch.None };
            dc.DrawRectangle(vb, null, new Rect(0, 0, size.Width, size.Height));
        }

        var rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out SystemTrayManager.RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO_TEST lpmi);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO_TEST
    {
        public int cbSize;
        public SystemTrayManager.RECT rcMonitor;
        public SystemTrayManager.RECT rcWork;
        public uint dwFlags;
    }

    [Fact]
    public void MainWindow_MaximizeAndResponsiveLayout_ShouldClampToWorkAreaAndAvoidClipping()
    {
        Exception? staException = null;
        string artifactDir = Environment.GetEnvironmentVariable("SCREENSUB_TRAY_ARTIFACT_DIR")
                             ?? Path.Combine(Path.GetTempPath(), "ScreenSubAI_TrayScreenshots");
        Directory.CreateDirectory(artifactDir);
        string maxPng = Path.Combine(artifactDir, "dashboard_maximized_verified.png");

        var sta = new Thread(() =>
        {
            try
            {
                var mockCapture = new Mock<IScreenCaptureService>();
                mockCapture.Setup(c => c.GetOpenWindows()).Returns([]);

                var mockCoord = new Mock<ISubtitleCoordinator>();
                mockCoord.SetupGet(c => c.State).Returns(SystemState.Idle);
                mockCoord.SetupGet(c => c.OutputAudioDevices).Returns([]);
                mockCoord.SetupGet(c => c.ActiveProviderBadgeText).Returns("OFFLINE AI — LOCAL PROCESSING");
                mockCoord.SetupGet(c => c.OcrStatusText).Returns("Sẵn sàng (RapidOCR ONNX)");
                mockCoord.SetupGet(c => c.TranslationStatusText).Returns("Đang kết nối Neural Translation Worker...");
                mockCoord.SetupGet(c => c.PipelineStatusText).Returns("Sẵn sàng");
                mockCoord.SetupGet(c => c.SpeechStatusText).Returns("Sẵn sàng");

                var mockSettings = new Mock<ISettingsService>();
                mockSettings.SetupGet(s => s.Settings).Returns(new AppSettings { MinimizeToTrayOnClose = false });
                mockSettings.Setup(s => s.GetSecret(It.IsAny<string>())).Returns(string.Empty);

                var overlay = new SubtitleOverlayWindow();
                var vm = new MainViewModel(
                    mockCapture.Object,
                    mockCoord.Object,
                    mockSettings.Object,
                    overlay,
                    () => throw new NotSupportedException());

                // Simulate realistic long status text like in the bug report screenshot
                vm.OcrStatusText = "Sẵn sàng (RapidOCR ONNX)";
                vm.TranslationStatusText = "Đang kết nối Neural Translation Worker...";

                var win = new MainWindow(vm)
                {
                    ShowActivated = false
                };

                win.MinHeight.Should().BeLessThanOrEqualTo(600, "1080p at 150% DPI has 680 DIP work area height");
                win.MinWidth.Should().BeLessThanOrEqualTo(1024);

                win.Show();
                win.WindowState = WindowState.Maximized;
                win.UpdateLayout();

                // Pump dispatcher briefly so WM_GETMINMAXINFO + StateChanged + SizeChanged settle
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);

                nint hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                hwnd.Should().NotBe(nint.Zero);

                GetWindowRect(hwnd, out var rcWin).Should().BeTrue();
                nint mon = MonitorFromWindow(hwnd, 2);
                var mi = new MONITORINFO_TEST { cbSize = Marshal.SizeOf<MONITORINFO_TEST>() };
                GetMonitorInfo(mon, ref mi).Should().BeTrue();

                // Must clamp cleanly to monitor work area (never overflow behind taskbar)
                rcWin.Left.Should().Be(mi.rcWork.Left);
                rcWin.Top.Should().Be(mi.rcWork.Top);
                rcWin.Right.Should().Be(mi.rcWork.Right);
                rcWin.Bottom.Should().Be(mi.rcWork.Bottom);

                // Verify maximize glyph flips to Restore ("❐")
                var glyph = win.FindName("MaximizeButtonGlyph") as TextBlock;
                glyph.Should().NotBeNull();
                glyph!.Text.Should().Be("❐");

                // Render crisp visual snapshot of the maximized RootLayoutGrid for visual verification
                var rootGrid = win.FindName("RootLayoutGrid") as FrameworkElement;
                rootGrid.Should().NotBeNull();
                double wDip = rootGrid!.ActualWidth > 0 ? rootGrid.ActualWidth : 1280;
                double hDip = rootGrid.ActualHeight > 0 ? rootGrid.ActualHeight : 680;
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(wDip * 1.5), (int)Math.Ceiling(hDip * 1.5), 144, 144, PixelFormats.Pbgra32);
                rtb.Render(rootGrid);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(maxPng)) enc.Save(fs);

                // Restore back to Normal and verify glyph flips back to "▢"
                win.WindowState = WindowState.Normal;
                win.UpdateLayout();
                glyph.Text.Should().Be("▢");

                win.Close();
                overlay.Close();
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();

        staException.Should().BeNull();
        File.Exists(maxPng).Should().BeTrue();
        new FileInfo(maxPng).Length.Should().BeGreaterThan(20_000);
    }
}
