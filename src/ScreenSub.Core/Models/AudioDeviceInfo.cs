namespace ScreenSub.Core.Models;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (Mặc định)" : Name;
}
