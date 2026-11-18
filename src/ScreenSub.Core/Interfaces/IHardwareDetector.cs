using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface IHardwareDetector
{
    HardwareInfo DetectHardware();
}

