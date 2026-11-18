namespace ScreenSub.Core.Models;

public sealed class OcrLine
{
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

public sealed class OcrResult
{
    public static readonly OcrResult Empty = new()
    {
        FullText = string.Empty,
        Lines = [],
        Confidence = 0,
        LatencyMs = 0,
        Timestamp = DateTime.UtcNow
    };

    public string FullText { get; set; } = string.Empty;
    public List<OcrLine> Lines { get; set; } = [];
    public double Confidence { get; set; }
    public long LatencyMs { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public bool HasText => !string.IsNullOrWhiteSpace(FullText);
}

