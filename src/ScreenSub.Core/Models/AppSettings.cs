using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Models;

public sealed class AppSettings
{
    // API Translation Settings
    public string AzureApiKey { get; set; } = string.Empty;
    public string AzureRegion { get; set; } = "eastasia";
    public string AzureEndpoint { get; set; } = "https://api.cognitive.microsofttranslator.com";
    public bool UseMockTranslation { get; set; } = true; // Default to mock until user provides real API key

    // Languages & Modes
    public SourceLanguage SourceLanguage { get; set; } = SourceLanguage.Auto;
    public List<TargetLanguage> TargetLanguages { get; set; } = [
        TargetLanguage.English,
        TargetLanguage.Chinese,
        TargetLanguage.Vietnamese
    ];
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Triple;

    // Capture & Region Settings
    public CaptureSourceType CaptureSource { get; set; } = CaptureSourceType.Window;
    public RegionMode RegionMode { get; set; } = RegionMode.FixedRegion;
    public ScreenRegion LastRegion { get; set; } = new(400, 700, 1120, 160);
    public string? LastWindowProcessName { get; set; }
    public string? LastWindowTitle { get; set; }
    public int CaptureFps { get; set; } = 4;

    // Overlay Window Settings
    public bool OverlayVisible { get; set; } = true;
    public double OverlayFontSize { get; set; } = 28.0;
    public string OverlayTextColor { get; set; } = "#FFFFFF";
    public double OverlayBackgroundOpacity { get; set; } = 0.70;
    public bool OverlayLocked { get; set; } = false;
    public bool OverlayClickThrough { get; set; } = true;
    public bool OverlayAlwaysOnTop { get; set; } = true;
    public double OverlayLeft { get; set; } = 360;
    public double OverlayTop { get; set; } = 840;
    public double OverlayWidth { get; set; } = 1200;
    public double OverlayHeight { get; set; } = 180;

    // OCR Worker Settings
    public string PythonWorkerPath { get; set; } = "workers/paddle-ocr/ocr_worker.py";
    public string PythonExePath { get; set; } = "python";
    public double FrameDiffThreshold { get; set; } = 0.03; // 3% pixel difference triggers OCR
    public int StabilityDebounceMs { get; set; } = 250;
}
