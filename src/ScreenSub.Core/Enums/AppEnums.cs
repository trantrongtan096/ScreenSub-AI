namespace ScreenSub.Core.Enums;

public enum CaptureSourceType
{
    FullScreen,
    Window
}

public enum RegionMode
{
    FixedRegion,
    FollowWindow
}

public enum DisplayMode
{
    Single,
    Dual,
    Triple
}

public enum SourceLanguage
{
    Auto,
    English,
    Chinese,
    Vietnamese
}

public enum TargetLanguage
{
    English,
    Chinese,
    Vietnamese
}

public enum SystemState
{
    Idle,
    Running,
    Paused,
    Error
}

public enum ServiceStatus
{
    Stopped,
    Starting,
    Running,
    Connected,
    Degraded,
    Error
}
