using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Models;

public enum CaptureErrorKind
{
    None,
    EmptyRegion,
    TargetWindowClosed,
    TargetWindowMinimized,
    OutOfBounds,
    OccludedBySelf,
    BlackFrame,
    ApiError
}

public sealed class CaptureFrameResult
{
    public static readonly CaptureFrameResult Empty = new()
    {
        ErrorKind = CaptureErrorKind.EmptyRegion,
        ErrorMessage = "Chưa chọn vùng chụp hợp lệ."
    };

    public byte[]? ImageBytes { get; init; }
    public bool IsSuccess => ImageBytes is { Length: > 0 } && ErrorKind == CaptureErrorKind.None;
    public CaptureErrorKind ErrorKind { get; init; } = CaptureErrorKind.None;
    public string ErrorMessage { get; init; } = string.Empty;
    public string BackendName { get; init; } = "Desktop BitBlt";
    public nint SourceHwnd { get; init; }
    public string SourceWindowTitle { get; init; } = string.Empty;
    public string SourceProcessName { get; init; } = string.Empty;
    public ScreenRegion RequestedRegion { get; init; } = ScreenRegion.Empty;
    public int FrameWidth { get; init; }
    public int FrameHeight { get; init; }
    public int CropWidth { get; init; }
    public int CropHeight { get; init; }
    public long CaptureLatencyMs { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public string FormattedSourceHwnd => SourceHwnd != nint.Zero ? $"0x{SourceHwnd:X}" : "Desktop (0x0)";
}
