namespace ScreenSub.Core.Models;

public sealed class WindowInfo
{
    public nint Handle { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public ScreenRegion Bounds { get; set; }
    public bool IsMinimized { get; set; }

    public string DisplayText => string.IsNullOrWhiteSpace(ProcessName)
        ? Title
        : $"{Title} ({ProcessName})";

    public override string ToString() => DisplayText;
}
