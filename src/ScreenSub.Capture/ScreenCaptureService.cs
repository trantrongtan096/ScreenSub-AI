using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ScreenSub.Core.Enums;
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
            // Check if our own Dashboard window is occluding the region on screen
            if (IsRegionOccludedByOwnWindow(region))
            {
                var underlyingWindow = FindTargetWindowForRegion(region);
                if (underlyingWindow != null && underlyingWindow.Handle != nint.Zero)
                {
                    var winCapture = TryCaptureWindowRoi(underlyingWindow.Handle, region);
                    if (winCapture.Bytes != null && !winCapture.IsBlack)
                    {
                        return winCapture.Bytes;
                    }
                }
            }

            var desktopCapture = TryCaptureDesktopRegion(region);
            return desktopCapture.Bytes;
        }, ct);
    }

    public Task<CaptureFrameResult> CaptureWithDiagnosticsAsync(
        ScreenRegion region,
        WindowInfo? targetWindow = null,
        RegionMode regionMode = RegionMode.FixedRegion,
        CaptureSourceType sourceType = CaptureSourceType.Window,
        CancellationToken ct = default)
    {
        if (region.IsEmpty || region.Width <= 0 || region.Height <= 0)
        {
            return Task.FromResult(CaptureFrameResult.Empty);
        }

        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();

            // 1. Validate target window if tracking or capturing a specific window
            nint targetHwnd = targetWindow?.Handle ?? nint.Zero;
            string targetTitle = targetWindow?.Title ?? string.Empty;
            string targetProcess = targetWindow?.ProcessName ?? string.Empty;

            if (targetHwnd != nint.Zero)
            {
                if (!NativeMethods.IsWindow(targetHwnd) || !NativeMethods.IsWindowVisible(targetHwnd))
                {
                    sw.Stop();
                    return new CaptureFrameResult
                    {
                        ErrorKind = CaptureErrorKind.TargetWindowClosed,
                        ErrorMessage = "Không lấy được hình ảnh từ cửa sổ đã chọn (Cửa sổ đã đóng hoặc bị ẩn).",
                        BackendName = "Window Direct",
                        SourceHwnd = targetHwnd,
                        SourceWindowTitle = targetTitle,
                        SourceProcessName = targetProcess,
                        RequestedRegion = region,
                        CaptureLatencyMs = sw.ElapsedMilliseconds
                    };
                }

                if (NativeMethods.IsIconic(targetHwnd))
                {
                    sw.Stop();
                    return new CaptureFrameResult
                    {
                        ErrorKind = CaptureErrorKind.TargetWindowMinimized,
                        ErrorMessage = "Cửa sổ đã chọn đang thu nhỏ (Minimized) — Tạm dừng nhận diện.",
                        BackendName = "Window Direct",
                        SourceHwnd = targetHwnd,
                        SourceWindowTitle = targetTitle,
                        SourceProcessName = targetProcess,
                        RequestedRegion = region,
                        CaptureLatencyMs = sw.ElapsedMilliseconds
                    };
                }
            }
            else
            {
                // Automatically discover the target window beneath the region (excluding ScreenSub AI itself)
                var discovered = FindTargetWindowForRegion(region);
                if (discovered != null)
                {
                    targetHwnd = discovered.Handle;
                    targetTitle = discovered.Title;
                    targetProcess = discovered.ProcessName;
                }
            }

            // 2. PRIMARY STRATEGY: Target Window Direct Capture (PrintWindow PW_RENDERFULLCONTENT)
            // This captures directly from DWM for the target application (Chrome/Edge/Player)
            // and is completely immune to ScreenSub AI Dashboard occlusion or other overlying windows!
            if (targetHwnd != nint.Zero)
            {
                var winResult = TryCaptureWindowRoi(targetHwnd, region);
                if (winResult.Bytes != null && !winResult.IsBlack)
                {
                    sw.Stop();
                    return new CaptureFrameResult
                    {
                        ImageBytes = winResult.Bytes,
                        ErrorKind = CaptureErrorKind.None,
                        ErrorMessage = string.Empty,
                        BackendName = "PrintWindow (DWM Direct)",
                        SourceHwnd = targetHwnd,
                        SourceWindowTitle = targetTitle,
                        SourceProcessName = targetProcess,
                        RequestedRegion = region,
                        FrameWidth = winResult.WindowWidth,
                        FrameHeight = winResult.WindowHeight,
                        CropWidth = region.Width,
                        CropHeight = region.Height,
                        CaptureLatencyMs = sw.ElapsedMilliseconds
                    };
                }
            }

            // 3. SECONDARY STRATEGY: Desktop Screen Capture (BitBlt)
            // Check if ScreenSub AI Dashboard is occluding the capture region on the desktop
            bool occludedBySelf = IsRegionOccludedByOwnWindow(region, targetHwnd);
            if (occludedBySelf)
            {
                sw.Stop();
                return new CaptureFrameResult
                {
                    ErrorKind = CaptureErrorKind.OccludedBySelf,
                    ErrorMessage = "Cửa sổ ScreenSub AI đang che khuất vùng phụ đề — Vui lòng di chuyển hoặc thu nhỏ Dashboard.",
                    BackendName = "Self-Occlusion Guard",
                    SourceHwnd = targetHwnd,
                    SourceWindowTitle = targetTitle,
                    SourceProcessName = targetProcess,
                    RequestedRegion = region,
                    CaptureLatencyMs = sw.ElapsedMilliseconds
                };
            }

            // 4. Validate region against virtual screen bounds
            var vScreen = GetVirtualScreenBounds();
            if (region.X < vScreen.X - 50 || region.Y < vScreen.Y - 50 ||
                region.Right > vScreen.Right + 50 || region.Bottom > vScreen.Bottom + 50)
            {
                sw.Stop();
                return new CaptureFrameResult
                {
                    ErrorKind = CaptureErrorKind.OutOfBounds,
                    ErrorMessage = $"Tọa độ vùng chụp ({region.X},{region.Y} {region.Width}×{region.Height}) nằm ngoài màn hình ({vScreen.Width}×{vScreen.Height}).",
                    BackendName = "Bounds Validator",
                    SourceHwnd = targetHwnd,
                    SourceWindowTitle = targetTitle,
                    SourceProcessName = targetProcess,
                    RequestedRegion = region,
                    FrameWidth = vScreen.Width,
                    FrameHeight = vScreen.Height,
                    CaptureLatencyMs = sw.ElapsedMilliseconds
                };
            }

            // 5. Perform Desktop Screen capture (BitBlt)
            var desktopResult = TryCaptureDesktopRegion(region);
            if (desktopResult.Bytes != null && !desktopResult.IsBlack)
            {
                sw.Stop();
                return new CaptureFrameResult
                {
                    ImageBytes = desktopResult.Bytes,
                    ErrorKind = CaptureErrorKind.None,
                    ErrorMessage = string.Empty,
                    BackendName = "Desktop BitBlt",
                    SourceHwnd = targetHwnd,
                    SourceWindowTitle = targetTitle,
                    SourceProcessName = targetProcess,
                    RequestedRegion = region,
                    FrameWidth = vScreen.Width,
                    FrameHeight = vScreen.Height,
                    CropWidth = region.Width,
                    CropHeight = region.Height,
                    CaptureLatencyMs = sw.ElapsedMilliseconds
                };
            }

            sw.Stop();

            if (desktopResult.Bytes != null && desktopResult.IsBlack)
            {
                return new CaptureFrameResult
                {
                    ImageBytes = desktopResult.Bytes,
                    ErrorKind = CaptureErrorKind.BlackFrame,
                    ErrorMessage = "Không lấy được hình ảnh từ cửa sổ đã chọn (Khung hình tối đen hoặc nội dung bảo vệ DRM).",
                    BackendName = "Desktop BitBlt",
                    SourceHwnd = targetHwnd,
                    SourceWindowTitle = targetTitle,
                    SourceProcessName = targetProcess,
                    RequestedRegion = region,
                    FrameWidth = vScreen.Width,
                    FrameHeight = vScreen.Height,
                    CropWidth = region.Width,
                    CropHeight = region.Height,
                    CaptureLatencyMs = sw.ElapsedMilliseconds
                };
            }

            return new CaptureFrameResult
            {
                ErrorKind = CaptureErrorKind.ApiError,
                ErrorMessage = "Không lấy được hình ảnh từ cửa sổ đã chọn (Lỗi API chụp màn hình).",
                BackendName = "Desktop BitBlt",
                SourceHwnd = targetHwnd,
                SourceWindowTitle = targetTitle,
                SourceProcessName = targetProcess,
                RequestedRegion = region,
                CaptureLatencyMs = sw.ElapsedMilliseconds
            };
        }, ct);
    }

    private static (byte[]? Bytes, bool IsBlack) TryCaptureDesktopRegion(ScreenRegion region)
    {
        nint hDesktopDC = NativeMethods.GetDC(nint.Zero);
        if (hDesktopDC == nint.Zero) return (null, true);

        nint hDestDC = NativeMethods.CreateCompatibleDC(hDesktopDC);
        nint hBitmap = NativeMethods.CreateCompatibleBitmap(hDesktopDC, region.Width, region.Height);
        nint hOldBitmap = NativeMethods.SelectObject(hDestDC, hBitmap);

        try
        {
            // Use SRCCOPY without CAPTUREBLT so transparent layered overlay windows do not leak into capture
            bool ok = NativeMethods.BitBlt(
                hDestDC, 0, 0, region.Width, region.Height,
                hDesktopDC, region.X, region.Y,
                NativeMethods.SRCCOPY);

            if (!ok)
            {
                return (null, true);
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            bitmapSource.Freeze();

            bool isBlack = IsBitmapAllBlack(bitmapSource);

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(ms);
            return (ms.ToArray(), isBlack);
        }
        catch
        {
            return (null, true);
        }
        finally
        {
            NativeMethods.SelectObject(hDestDC, hOldBitmap);
            NativeMethods.DeleteObject(hBitmap);
            NativeMethods.DeleteDC(hDestDC);
            NativeMethods.ReleaseDC(nint.Zero, hDesktopDC);
        }
    }

    private static (byte[]? Bytes, bool IsBlack, int WindowWidth, int WindowHeight) TryCaptureWindowRoi(nint hwnd, ScreenRegion screenRegion)
    {
        if (hwnd == nint.Zero || !NativeMethods.GetWindowRect(hwnd, out var winRect))
        {
            return (null, true, 0, 0);
        }

        int winW = winRect.Width;
        int winH = winRect.Height;
        if (winW <= 0 || winH <= 0)
        {
            return (null, true, 0, 0);
        }

        // Convert screen-space region coordinates to window-space coordinates
        int cropX = Math.Clamp(screenRegion.X - winRect.Left, 0, Math.Max(0, winW - 1));
        int cropY = Math.Clamp(screenRegion.Y - winRect.Top, 0, Math.Max(0, winH - 1));
        int cropW = Math.Clamp(screenRegion.Width, 1, winW - cropX);
        int cropH = Math.Clamp(screenRegion.Height, 1, winH - cropY);

        if (cropW < 10 || cropH < 8)
        {
            return (null, true, winW, winH);
        }

        nint hDesktopDC = NativeMethods.GetDC(nint.Zero);
        if (hDesktopDC == nint.Zero) return (null, true, winW, winH);

        nint hWinDC = NativeMethods.CreateCompatibleDC(hDesktopDC);
        nint hWinBmp = NativeMethods.CreateCompatibleBitmap(hDesktopDC, winW, winH);
        nint hOldWinBmp = NativeMethods.SelectObject(hWinDC, hWinBmp);

        nint hCropDC = NativeMethods.CreateCompatibleDC(hDesktopDC);
        nint hCropBmp = NativeMethods.CreateCompatibleBitmap(hDesktopDC, cropW, cropH);
        nint hOldCropBmp = NativeMethods.SelectObject(hCropDC, hCropBmp);

        try
        {
            // Render full DWM window surface (works on Chrome/Edge even when occluded by another window)
            bool printed = NativeMethods.PrintWindow(hwnd, hWinDC, NativeMethods.PW_RENDERFULLCONTENT);
            if (!printed)
            {
                printed = NativeMethods.PrintWindow(hwnd, hWinDC, 0);
            }

            if (!printed)
            {
                return (null, true, winW, winH);
            }

            // Crop only the requested ROI
            NativeMethods.BitBlt(hCropDC, 0, 0, cropW, cropH, hWinDC, cropX, cropY, NativeMethods.SRCCOPY);

            var bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                hCropBmp,
                nint.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            bitmapSource.Freeze();

            bool isBlack = IsBitmapAllBlack(bitmapSource);
            if (isBlack)
            {
                // Try fallback PrintWindow with flag 0 if PW_RENDERFULLCONTENT returned all-black
                if (NativeMethods.PrintWindow(hwnd, hWinDC, 0))
                {
                    NativeMethods.BitBlt(hCropDC, 0, 0, cropW, cropH, hWinDC, cropX, cropY, NativeMethods.SRCCOPY);
                    bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                        hCropBmp,
                        nint.Zero,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    bitmapSource.Freeze();
                    isBlack = IsBitmapAllBlack(bitmapSource);
                }
            }

            using var ms = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
            encoder.Save(ms);
            return (ms.ToArray(), isBlack, winW, winH);
        }
        catch
        {
            return (null, true, winW, winH);
        }
        finally
        {
            NativeMethods.SelectObject(hCropDC, hOldCropBmp);
            NativeMethods.DeleteObject(hCropBmp);
            NativeMethods.DeleteDC(hCropDC);

            NativeMethods.SelectObject(hWinDC, hOldWinBmp);
            NativeMethods.DeleteObject(hWinBmp);
            NativeMethods.DeleteDC(hWinDC);

            NativeMethods.ReleaseDC(nint.Zero, hDesktopDC);
        }
    }

    private static bool IsBitmapAllBlack(BitmapSource source)
    {
        try
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;
            if (w <= 0 || h <= 0) return true;

            int bytesPerPixel = (source.Format.BitsPerPixel + 7) / 8;
            if (bytesPerPixel < 3) return false;

            int stride = w * bytesPerPixel;
            byte[] pixels = new byte[h * stride];
            source.CopyPixels(pixels, stride, 0);

            // Sample up to 1024 points across the image
            int totalBytes = pixels.Length;
            int step = Math.Max(bytesPerPixel, (totalBytes / 1024 / bytesPerPixel) * bytesPerPixel);

            for (int i = 0; i + 2 < totalBytes; i += step)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];
                if (r > 3 || g > 3 || b > 3)
                {
                    return false; // Found non-black pixel
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsRegionOccludedByOwnWindow(ScreenRegion region) =>
        IsRegionOccludedByOwnWindow(region, nint.Zero);

    public bool IsRegionOccludedByOwnWindow(ScreenRegion region, nint targetHwnd)
    {
        if (region.IsEmpty) return false;

        int currentPid = Environment.ProcessId;
        bool occludedByOwnDashboard = false;
        bool foundTargetFirst = false;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            if (IsWindowCloaked(hwnd))
            {
                return true;
            }

            if (targetHwnd != nint.Zero && hwnd == targetHwnd)
            {
                foundTargetFirst = true;
                return false; // Stop enumeration: target window is above our Dashboard in Z-order!
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid)
            {
                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                string title = sb.ToString();

                // Skip our transparent overlay and region selection windows (they use WDA_EXCLUDEFROMCAPTURE)
                if (title.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("RegionSelection", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                // This is our MainWindow (Dashboard)! Check if its bounds intersect the capture region
                var dashBounds = GetWindowBounds(hwnd);
                if (!dashBounds.IsEmpty && Intersects(dashBounds, region))
                {
                    occludedByOwnDashboard = true;
                    return false;
                }
            }

            return true;
        }, nint.Zero);

        return !foundTargetFirst && occludedByOwnDashboard;
    }

    public WindowInfo? GetForegroundTargetWindow()
    {
        nint fg = NativeMethods.GetForegroundWindow();
        if (fg == nint.Zero) return null;

        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId)
        {
            return null; // Never return ScreenSub AI itself as target window!
        }

        return GetWindowInfo(fg);
    }

    public WindowInfo? FindTargetWindowForRegion(ScreenRegion region)
    {
        if (region.IsEmpty) return null;

        int currentPid = Environment.ProcessId;
        int centerX = region.X + region.Width / 2;
        int centerY = region.Y + region.Height / 2;

        WindowInfo? bestMatch = null;

        // EnumWindows iterates in Z-order from topmost to bottommost
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
            {
                return true;
            }

            if (IsWindowCloaked(hwnd))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid)
            {
                return true; // Strictly skip all ScreenSub AI windows
            }

            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (IsShellOrBackgroundWindow(hwnd, title))
            {
                return true;
            }

            var bounds = GetWindowBounds(hwnd);
            if (bounds.Width < 100 || bounds.Height < 100)
            {
                return true;
            }

            // Check if this window contains the center of the selected region or overlaps it significantly
            bool containsCenter = centerX >= bounds.X && centerX <= bounds.Right &&
                                  centerY >= bounds.Y && centerY <= bounds.Bottom;

            if (containsCenter || GetIntersectionArea(bounds, region) >= (region.Width * region.Height) / 2)
            {
                string processName = string.Empty;
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    processName = proc.ProcessName;
                }
                catch { }

                bestMatch = new WindowInfo
                {
                    Handle = hwnd,
                    Title = title,
                    ProcessName = processName,
                    Bounds = bounds,
                    IsMinimized = false
                };
                return false; // Found the topmost window at the selected region!
            }

            return true;
        }, nint.Zero);

        return bestMatch;
    }

    public void ActivateWindow(nint handle)
    {
        if (handle == nint.Zero || !NativeMethods.IsWindow(handle)) return;
        try
        {
            NativeMethods.SetForegroundWindow(handle);
        }
        catch { }
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

            if (IsWindowCloaked(hwnd))
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

            if (IsShellOrBackgroundWindow(hwnd, title))
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
        if (handle == nint.Zero || !NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle))
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(handle, out uint pid);
        if (pid == Environment.ProcessId)
        {
            return null; // Never allow tracking our own process window
        }

        var bounds = GetWindowBounds(handle);
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(handle, sb, sb.Capacity);
        var title = sb.ToString().Trim();

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

    private static bool IsWindowCloaked(nint hwnd)
    {
        int hr = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_CLOAKED,
            out int cloaked,
             sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static bool IsShellOrBackgroundWindow(nint hwnd, string title)
    {
        if (title is "Program Manager" or "Windows Input Experience" or "Settings" or "Microsoft Text Input Application")
        {
            return true;
        }

        var cls = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        string className = cls.ToString();
        return className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    private static bool Intersects(ScreenRegion a, ScreenRegion b)
    {
        return a.X < b.Right && a.Right > b.X &&
               a.Y < b.Bottom && a.Bottom > b.Y;
    }

    private static long GetIntersectionArea(ScreenRegion a, ScreenRegion b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.Right, b.Right);
        int bottom = Math.Min(a.Bottom, b.Bottom);

        if (right <= left || bottom <= top) return 0;
        return (long)(right - left) * (bottom - top);
    }

    private static ScreenRegion GetWindowBounds(nint hwnd)
    {
        // Try DwmGetWindowAttribute for exact visual bounds (without invisible drop shadows)
        int hr = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT rect,
            Marshal.SizeOf<NativeMethods.RECT>());

        if (hr == 0 && rect.Width > 0 && rect.Height > 0)
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
