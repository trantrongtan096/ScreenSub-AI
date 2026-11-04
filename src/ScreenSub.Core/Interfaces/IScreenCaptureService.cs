using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface IScreenCaptureService
{
    Task<byte[]?> CaptureRegionAsync(ScreenRegion region, CancellationToken ct = default);
    IReadOnlyList<WindowInfo> GetOpenWindows();
    WindowInfo? GetWindowInfo(nint handle);
    ScreenRegion GetVirtualScreenBounds();
}
