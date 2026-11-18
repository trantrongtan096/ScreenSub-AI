namespace ScreenSub.Core.Interfaces;

public interface IFrameChangeDetector
{
    bool HasFrameChanged(byte[] currentFrameBytes, double threshold = 0.03);
    void Reset();
}

