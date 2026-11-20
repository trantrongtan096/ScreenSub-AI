namespace ScreenSub.Core.Models;

public sealed class SpeechSegment
{
    public long Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public long StartTimeMs { get; init; }
    public long EndTimeMs { get; init; }
    public byte[] PcmData { get; init; } = Array.Empty<byte>();
    public int SampleRate { get; init; } = 16000;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public double DurationMs => (double)PcmData.Length / (SampleRate * 2) * 1000.0;
}
