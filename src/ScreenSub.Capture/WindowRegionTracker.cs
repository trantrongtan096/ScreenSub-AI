using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Capture;

public sealed class WindowRegionTracker
{
    private readonly IScreenCaptureService _captureService;
    private WindowInfo? _trackedWindow;
    private double _relativeX;
    private double _relativeY;
    private double _relativeWidth;
    private double _relativeHeight;

    public WindowRegionTracker(IScreenCaptureService captureService)
    {
        _captureService = captureService;
    }

    public bool IsTracking => _trackedWindow != null && _trackedWindow.Handle != nint.Zero;
    public WindowInfo? TrackedWindow => _trackedWindow;

    public void SetTargetWindow(WindowInfo window, ScreenRegion selectedScreenRegion)
    {
        _trackedWindow = window;

        var windowBounds = window.Bounds;
        if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
        {
            _relativeX = 0;
            _relativeY = 0;
            _relativeWidth = 1;
            _relativeHeight = 1;
            return;
        }

        // Calculate normalized coordinates [0.0 - 1.0] relative to window frame
        _relativeX = Math.Clamp((double)(selectedScreenRegion.X - windowBounds.X) / windowBounds.Width, 0.0, 0.99);
        _relativeY = Math.Clamp((double)(selectedScreenRegion.Y - windowBounds.Y) / windowBounds.Height, 0.0, 0.99);
        _relativeWidth = Math.Clamp((double)selectedScreenRegion.Width / windowBounds.Width, 0.01, 1.0 - _relativeX);
        _relativeHeight = Math.Clamp((double)selectedScreenRegion.Height / windowBounds.Height, 0.01, 1.0 - _relativeY);
    }

    public (ScreenRegion Region, bool IsValid, string StatusMessage) GetCurrentRegion()
    {
        if (_trackedWindow == null || _trackedWindow.Handle == nint.Zero)
        {
            return (ScreenRegion.Empty, false, "Chưa chọn cửa sổ ứng dụng.");
        }

        var currentInfo = _captureService.GetWindowInfo(_trackedWindow.Handle);
        if (currentInfo == null)
        {
            return (ScreenRegion.Empty, false, "Không lấy được hình ảnh từ cửa sổ đã chọn (Cửa sổ đã đóng).");
        }

        if (currentInfo.IsMinimized)
        {
            return (ScreenRegion.Empty, false, "Cửa sổ đang thu nhỏ (Minimized) - Tạm dừng capture.");
        }

        var bounds = currentInfo.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return (ScreenRegion.Empty, false, "Kích thước cửa sổ không hợp lệ.");
        }

        _trackedWindow = currentInfo;

        int targetX = bounds.X + (int)Math.Round(_relativeX * bounds.Width);
        int targetY = bounds.Y + (int)Math.Round(_relativeY * bounds.Height);
        int targetW = Math.Max(10, (int)Math.Round(_relativeWidth * bounds.Width));
        int targetH = Math.Max(10, (int)Math.Round(_relativeHeight * bounds.Height));

        return (new ScreenRegion(targetX, targetY, targetW, targetH), true, "Theo dõi cửa sổ bình thường.");
    }

    public void Reset()
    {
        _trackedWindow = null;
    }
}
