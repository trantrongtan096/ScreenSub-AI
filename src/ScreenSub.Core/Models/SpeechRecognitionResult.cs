namespace ScreenSub.Core.Models;

public sealed class SpeechRecognitionResult
{
    public long SegmentId { get; init; }
    public string Text { get; init; } = string.Empty;
    public string DetectedLanguage { get; init; } = string.Empty;
    public bool IsFinal { get; init; } = true;
    public double Confidence { get; init; } = 1.0;
    public long LatencyMs { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public static SpeechRecognitionResult Empty => new()
    {
        SegmentId = 0,
        Text = string.Empty,
        DetectedLanguage = string.Empty,
        IsFinal = true,
        Confidence = 0,
        LatencyMs = 0
    };
}
