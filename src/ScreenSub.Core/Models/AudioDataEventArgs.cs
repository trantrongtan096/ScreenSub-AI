namespace ScreenSub.Core.Models;

public sealed class AudioDataEventArgs : EventArgs
{
    public byte[] Pcm16Data { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public DateTime Timestamp { get; }

    public AudioDataEventArgs(byte[] pcm16Data, int sampleRate = 16000, int channels = 1)
    {
        Pcm16Data = pcm16Data;
        SampleRate = sampleRate;
        Channels = channels;
        Timestamp = DateTime.UtcNow;
    }
}
