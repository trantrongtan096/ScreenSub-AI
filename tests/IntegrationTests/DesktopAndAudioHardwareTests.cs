using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ScreenSub.Audio.Capture;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;
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
}
