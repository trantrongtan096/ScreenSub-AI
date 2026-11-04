using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    public ScreenRegion GetVirtualScreenBounds()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        return new ScreenRegion(x, y, w, h);
    }

    public Task<byte[]?> CaptureRegionAsync(ScreenRegion region, CancellationToken ct = default)
    {
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0)
        {
            return Task.FromResult<byte[]?>(null);
        }

        return Task.Run(() =>
        {
            nint hDesktopDC = NativeMethods.GetDC(nint.Zero);
            if (hDesktopDC == nint.Zero) return null;

            nint hDestDC = NativeMethods.CreateCompatibleDC(hDesktopDC);
            nint hBitmap = NativeMethods.CreateCompatibleBitmap(hDesktopDC, region.Width, region.Height);
            nint hOldBitmap = NativeMethods.SelectObject(hDestDC, hBitmap);

            try
            {
                // BitBlt with SRCCOPY | CAPTUREBLT to capture layered/transparent elements
                NativeMethods.BitBlt(
                    hDestDC, 0, 0, region.Width, region.Height,
                    hDesktopDC, region.X, region.Y,
                    NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

                // Convert HBitmap to WPF BitmapSource
                var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    nint.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                bitmapSource.Freeze();

                using var ms = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                encoder.Save(ms);
                return ms.ToArray();
            }
            finally
            {
                NativeMethods.SelectObject(hDestDC, hOldBitmap);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(hDestDC);
                NativeMethods.ReleaseDC(nint.Zero, hDesktopDC);
            }
        }, ct);
    }

    public IReadOnlyList<WindowInfo> GetOpenWindows()
    {
        var windows = new List<WindowInfo>();
        var currentPid = Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid)
            {
                return true; // Skip our own app windows
            }

            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            // Exclude common background/shell windows
            if (title == "Program Manager" || title == "Windows Input Experience" || title == "Settings")
            {
                return true;
            }

            var bounds = GetWindowBounds(hwnd);
            if (bounds.Width < 100 || bounds.Height < 100)
            {
                return true;
            }

            string processName = string.Empty;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                processName = proc.ProcessName;
            }
            catch
            {
                // Ignore process access denied
            }

            windows.Add(new WindowInfo
            {
                Handle = hwnd,
                Title = title,
                ProcessName = processName,
                Bounds = bounds,
                IsMinimized = NativeMethods.IsIconic(hwnd)
            });

            return true;
        }, nint.Zero);

        return windows.OrderBy(w => w.Title).ToList();
    }

    public WindowInfo? GetWindowInfo(nint handle)
    {
        if (handle == nint.Zero || !NativeMethods.IsWindowVisible(handle))
        {
            return null;
        }

        var bounds = GetWindowBounds(handle);
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(handle, sb, sb.Capacity);
        var title = sb.ToString().Trim();

        NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
        string processName = string.Empty;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            processName = proc.ProcessName;
        }
        catch { }

        return new WindowInfo
        {
            Handle = handle,
            Title = title,
            ProcessName = processName,
            Bounds = bounds,
            IsMinimized = NativeMethods.IsIconic(handle)
        };
    }

    private static ScreenRegion GetWindowBounds(nint hwnd)
    {
        // Try DwmGetWindowAttribute for exact visual bounds (without invisible drop shadows)
        int hr = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out var rect,
            Marshal.SizeOf<NativeMethods.RECT>());

        if (hr == 0)
        {
            return new ScreenRegion(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        // Fallback to GetWindowRect
        if (NativeMethods.GetWindowRect(hwnd, out rect))
        {
            return new ScreenRegion(rect.Left, rect.Top, rect.Width, rect.Height);
        }

        return ScreenRegion.Empty;
    }
}
