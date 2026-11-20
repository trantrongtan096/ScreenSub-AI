namespace ScreenSub.Core.Models;

public sealed class AudioLevelEventArgs : EventArgs
{
    public float PeakLevel { get; }
    public float RmsLevel { get; }
    public float Decibels { get; }
    public bool HasSound { get; }

    public AudioLevelEventArgs(float peakLevel, float rmsLevel, float decibels, bool hasSound)
    {
        PeakLevel = peakLevel;
        RmsLevel = rmsLevel;
        Decibels = decibels;
        HasSound = hasSound;
    }
}
