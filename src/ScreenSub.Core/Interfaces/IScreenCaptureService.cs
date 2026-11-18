using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface IScreenCaptureService
{
    Task<byte[]?> CaptureRegionAsync(ScreenRegion region, CancellationToken ct = default);
    Task<CaptureFrameResult> CaptureWithDiagnosticsAsync(
        ScreenRegion region,
        WindowInfo? targetWindow = null,
        RegionMode regionMode = RegionMode.FixedRegion,
        CaptureSourceType sourceType = CaptureSourceType.Window,
        CancellationToken ct = default);
    IReadOnlyList<WindowInfo> GetOpenWindows();
    WindowInfo? GetWindowInfo(nint handle);
    WindowInfo? GetForegroundTargetWindow();
    WindowInfo? FindTargetWindowForRegion(ScreenRegion region);
    bool IsRegionOccludedByOwnWindow(ScreenRegion region);
    void ActivateWindow(nint handle);
    ScreenRegion GetVirtualScreenBounds();
}
