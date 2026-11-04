using ScreenSub.Core.Interfaces;

namespace ScreenSub.Capture;

public sealed class FrameChangeDetector : IFrameChangeDetector
{
    private byte[]? _previousSample;

    public bool HasFrameChanged(byte[] currentFrameBytes, double threshold = 0.03)
    {
        if (currentFrameBytes == null || currentFrameBytes.Length == 0)
        {
            return false;
        }

        // Subsample the bytes to keep comparison ultra-fast (under 1ms)
        const int sampleSize = 1024;
        byte[] currentSample = new byte[Math.Min(sampleSize, currentFrameBytes.Length)];
        int step = Math.Max(1, currentFrameBytes.Length / currentSample.Length);

        for (int i = 0; i < currentSample.Length; i++)
        {
            int index = i * step;
            if (index < currentFrameBytes.Length)
            {
                currentSample[i] = currentFrameBytes[index];
            }
        }

        if (_previousSample == null || _previousSample.Length != currentSample.Length)
        {
            _previousSample = currentSample;
            return true; // First frame is always considered changed
        }

        int diffCount = 0;
        for (int i = 0; i < currentSample.Length; i++)
        {
            if (Math.Abs(currentSample[i] - _previousSample[i]) > 15)
            {
                diffCount++;
            }
        }

        double diffRatio = (double)diffCount / currentSample.Length;

        if (diffRatio >= threshold)
        {
            _previousSample = currentSample;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _previousSample = null;
    }
}
