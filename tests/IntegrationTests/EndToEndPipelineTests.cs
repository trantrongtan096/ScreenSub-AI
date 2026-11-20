using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using Moq;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Core.Processing;
using ScreenSub.Infrastructure;
using ScreenSub.Ocr;
using ScreenSub.Overlay;
using ScreenSub.Translation;
using Xunit;

namespace IntegrationTests;

public class EndToEndPipelineTests : IAsyncLifetime
{
    private PaddleOcrEngine _ocrEngine = null!;

    public async Task InitializeAsync()
    {
        _ocrEngine = new PaddleOcrEngine();
        await _ocrEngine.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _ocrEngine.DisposeAsync();
    }

    private static string GetAssetPath(string fileName)
    {
        // Search upward to find test_assets directory
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "test_assets", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        string cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "test_assets", fileName);
        if (File.Exists(cwdCandidate)) return cwdCandidate;

        throw new FileNotFoundException($"Cannot find test asset: {fileName}");
    }

    [Fact]
    public async Task TestA_ChineseSubtitles_RapidOcr_ShouldRecognizeChineseText()
    {
        string path = GetAssetPath("chinese_subtitle.png");
        byte[] imageBytes = await File.ReadAllBytesAsync(path);

        _ocrEngine.Status.Should().Be(ServiceStatus.Running, because: _ocrEngine.StatusMessage);

        var ocrResult = await _ocrEngine.RecognizeAsync(imageBytes);

        ocrResult.HasText.Should().BeTrue(because: $"Status: {_ocrEngine.StatusMessage}, Text: '{ocrResult.FullText}'");
        ocrResult.FullText.Should().Contain("你今天有时间吗？");
        ocrResult.Confidence.Should().BeGreaterThan(0.85);
        ocrResult.LatencyMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TestB_EnglishSubtitles_RapidOcr_ShouldRecognizeEnglishText()
    {
        string path = GetAssetPath("english_subtitle.png");
        byte[] imageBytes = await File.ReadAllBytesAsync(path);

        var ocrResult = await _ocrEngine.RecognizeAsync(imageBytes);

        ocrResult.HasText.Should().BeTrue();
        ocrResult.FullText.Should().Contain("Where");
        ocrResult.FullText.Should().Contain("going");
        ocrResult.Confidence.Should().BeGreaterThan(0.85);
    }

    [Fact]
    public async Task TestC_VietnameseSubtitles_RapidOcr_ShouldRecognizeVietnameseText()
    {
        string path = GetAssetPath("vietnamese_subtitle.png");
        byte[] imageBytes = await File.ReadAllBytesAsync(path);

        var ocrResult = await _ocrEngine.RecognizeAsync(imageBytes);

        ocrResult.HasText.Should().BeTrue();
        ocrResult.FullText.Should().Contain("Xin");
        ocrResult.FullText.Should().Contain("Nam");
    }

    [Fact]
    public async Task TestD_NoSubtitle_BlankFrame_ShouldDetectNoTextAndNotTriggerTranslation()
    {
        string path = GetAssetPath("blank_frame.png");
        byte[] imageBytes = await File.ReadAllBytesAsync(path);

        var ocrResult = await _ocrEngine.RecognizeAsync(imageBytes);

        ocrResult.HasText.Should().BeFalse();
        ocrResult.FullText.Should().BeEmpty();
    }

    [Fact]
    public void TestE_SubtitleStability_DebounceAndDeduplication_ShouldSuppressJitter()
    {
        var normalizer = new TextNormalizer();

        var first = normalizer.ApplyStabilityFilter("Where are you going?");
        first.HasChanged.Should().BeTrue();
        first.StableText.Should().Be("Where are you going?");

        // Identical text -> HasChanged = false
        var identical = normalizer.ApplyStabilityFilter("Where are you going?");
        identical.HasChanged.Should().BeFalse();

        // Minor 1-character OCR jitter within debounce window -> suppressed
        var jitter = normalizer.ApplyStabilityFilter("Where are you going!");
        jitter.HasChanged.Should().BeFalse();
        jitter.StableText.Should().Be("Where are you going?");
    }

    [Fact]
    public async Task TestF_AzureTranslation_SameLanguageFiltering_ShouldSkipApiCall()
    {
        var settings = new SettingsService();
        var cache = new TranslationCache();
        var provider = new AzureTranslatorProvider(new HttpClient(), settings, cache);

        // Request English -> English: effective targets empty -> returns immediate without API Key
        var result = await provider.TranslateAsync("Hello World", SourceLanguage.English, [TargetLanguage.English]);

        result.Should().NotBeNull();
        result.OriginalText.Should().Be("Hello World");
        result.Translations[TargetLanguage.English].Should().Be("Hello World");
        result.LatencyMs.Should().Be(0);
    }

    [Fact]
    public void TestG_PauseAndResume_ShouldTransitionStateCleanly()
    {
        var settings = new SettingsService();
        var capture = new ScreenSub.Capture.ScreenCaptureService();
        var changeDetector = new ScreenSub.Capture.FrameChangeDetector();
        var cache = new TranslationCache();
        var azure = new AzureTranslatorProvider(new HttpClient(), settings, cache);
        var mock = new MockTranslationProvider();

        var coordinator = new ScreenSub.Desktop.Services.SubtitleCoordinator(
            capture, _ocrEngine, azure, mock, settings, changeDetector);

        coordinator.Start();
        coordinator.State.Should().Be(SystemState.Running);

        coordinator.Pause();
        coordinator.State.Should().Be(SystemState.Paused);
        coordinator.PipelineStatusText.Should().Contain("Tạm dừng");

        coordinator.Start(); // Resume
        coordinator.State.Should().Be(SystemState.Running);

        coordinator.Stop();
        coordinator.State.Should().BeOneOf(SystemState.Idle, SystemState.Stopped);
    }

    private static byte[] RenderBilingualYouTubeSubtitleFrame(string englishLine, string chineseLine)
    {
        using var bmp = new Bitmap(640, 160, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(24, 28, 36));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var enFont = new Font("Segoe UI", 26f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var zhFont = new Font("Microsoft YaHei", 30f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var whiteBrush = new SolidBrush(Color.White);
        using var sf = new StringFormat { Alignment = StringAlignment.Center };

        g.DrawString(englishLine, enFont, whiteBrush, new RectangleF(0, 28, 640, 48), sf);
        g.DrawString(chineseLine, zhFont, whiteBrush, new RectangleF(0, 88, 640, 52), sf);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    [Fact]
    public async Task TestH_YouTubeBilingualSubtitle_RealOcrAndOfflineM2M100_ShouldRecognizeAndTranslateToVietnamese()
    {
        byte[] frameBytes = RenderBilingualYouTubeSubtitleFrame("The mountain roads twisted", "山路一路蜿蜒，");

        // 1. Real RapidOCR ONNX recognition
        var ocrResult = await _ocrEngine.RecognizeAsync(frameBytes);
        ocrResult.HasText.Should().BeTrue();

        // 2. Bilingual segmentation & CJK OCR homograph normalization (Auto mode: top line is primary, bottom line is secondary)
        var autoAnalysis = SubtitleTextProcessor.AnalyzeOcrText(ocrResult.FullText, SourceLanguage.Auto);
        autoAnalysis.PrimarySourceText.Should().Be("The mountain roads twisted");
        autoAnalysis.DetectedPrimaryLanguage.Should().Be("en");
        autoAnalysis.ExistingSecondaryText.Should().Be("山路一路蜿蜒，");
        autoAnalysis.ExistingSecondaryLanguage.Should().Be("zh");

        // Also verify explicit Chinese source mode selects the Chinese line as primary
        var zhAnalysis = SubtitleTextProcessor.AnalyzeOcrText(ocrResult.FullText, SourceLanguage.Chinese);
        zhAnalysis.PrimarySourceText.Should().Be("山路一路蜿蜒，");
        zhAnalysis.DetectedPrimaryLanguage.Should().Be("zh");
        zhAnalysis.ExistingSecondaryText.Should().Be("The mountain roads twisted");
        zhAnalysis.ExistingSecondaryLanguage.Should().Be("en");

        // 3. Real Offline M2M-100 418M INT8 Neural Translation to Vietnamese
        await using var neuralEngine = new LocalNeuralTranslationEngine();
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), $"ScreenSubTest_{Guid.NewGuid():N}"));
        settings.Settings.TranslationEngineMode = TranslationEngineMode.Offline;
        var provider = new LocalTranslationProvider(neuralEngine, new TranslationCache(), settings);

        var transResult = await provider.TranslateAsync(
            zhAnalysis.PrimarySourceText,
            SourceLanguage.Chinese,
            [TargetLanguage.Vietnamese]);

        string vi = SubtitleTextProcessor.CleanTranslationOutput(transResult.GetTranslation(TargetLanguage.Vietnamese));
        vi.Should().NotBeNullOrWhiteSpace();
        vi.ToLowerInvariant().Should().Contain("đường núi");
        transResult.LatencyMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task TestI_AzureMissingApiKey_ShouldStillDisplayRecognizedOcrTextAndReportUnconfiguredBadge()
    {
        byte[] frameBytes = RenderBilingualYouTubeSubtitleFrame("The mountain roads twisted", "山路一路蜿蜒，");

        var tempDir = Path.Combine(Path.GetTempPath(), $"ScreenSubTest_{Guid.NewGuid():N}");
        var settings = new SettingsService(tempDir);
        settings.Settings.TranslationEngineMode = TranslationEngineMode.Online;
        settings.Settings.HasExplicitlySelectedOnlineMode = true;
        settings.Settings.AzureApiKey = string.Empty;
        settings.Settings.UseMockTranslation = false;
        settings.Settings.TargetLanguages = [TargetLanguage.English, TargetLanguage.Chinese, TargetLanguage.Vietnamese];

        var cache = new TranslationCache();
        await using var localEngine = new LocalNeuralTranslationEngine();
        var localProvider = new LocalTranslationProvider(localEngine, cache, settings);
        var azureProvider = new AzureTranslatorProvider(new HttpClient(), settings, cache);
        var mockProvider = new MockTranslationProvider();
        var resolver = new TranslationProviderResolver(settings, localProvider, azureProvider, mockProvider);

        resolver.ActiveProviderBadgeText.Should().Be("AZURE — CHƯA CẤU HÌNH");
        azureProvider.IsConfigured.Should().BeFalse();

        var mockCapture = new Mock<IScreenCaptureService>();
        mockCapture
            .Setup(c => c.CaptureWithDiagnosticsAsync(
                It.IsAny<ScreenRegion>(),
                It.IsAny<WindowInfo?>(),
                It.IsAny<RegionMode>(),
                It.IsAny<CaptureSourceType>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CaptureFrameResult
            {
                ImageBytes = frameBytes,
                ErrorKind = CaptureErrorKind.None,
                BackendName = "PrintWindow(PW_RENDERFULLCONTENT)",
                RequestedRegion = new ScreenRegion(444, 806, 458, 85),
                CropWidth = 458,
                CropHeight = 85,
                CaptureLatencyMs = 8
            });

        await using var coordinator = new ScreenSub.Desktop.Services.SubtitleCoordinator(
            mockCapture.Object,
            _ocrEngine,
            resolver,
            settings,
            new ScreenSub.Capture.FrameChangeDetector());

        string recognizedOcrText = string.Empty;
        SubtitleItem? lastSubtitle = null;
        coordinator.OcrTextRecognized += (_, info) => recognizedOcrText = info.Text;
        coordinator.SubtitleUpdated += (_, sub) =>
        {
            if (sub.HasContent) lastSubtitle = sub;
        };

        coordinator.UpdateSelectedRegionAndAutoStart(new ScreenRegion(444, 806, 458, 85));

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && lastSubtitle == null)
        {
            await Task.Delay(100);
        }

        coordinator.Stop();

        // Even though Azure API Key is missing, OCR text MUST be recognized and surfaced!
        recognizedOcrText.Should().Be("The mountain roads twisted");
        lastSubtitle.Should().NotBeNull();
        lastSubtitle!.OriginalText.Should().Be("The mountain roads twisted");
        lastSubtitle.EnglishText.Should().Be("The mountain roads twisted");
        lastSubtitle.ChineseText.Should().Be("山路一路蜿蜒，");
        lastSubtitle.VietnameseText.Should().Contain("Azure chưa cấu hình");

        // End-to-End latency MUST NOT report fake ~10ms when translation failed
        coordinator.AverageLatencyMs.Should().Be(0);

        // Overlay filter must reject bracketed error placeholder when only Vietnamese is displayed
        SubtitleOverlayWindow.HasValidDisplayableSubtitle(
            new SubtitleItem
            {
                OriginalText = "山路一路蜿蜒，",
                OriginalLanguageCode = "zh",
                VietnameseText = lastSubtitle.VietnameseText
            }).Should().BeFalse();
    }

    [Fact]
    public void TestJ_OverlayVisibilityAndTestMode_ShouldShowTopmostAndClampToMonitorWorkArea()
    {
        Exception? threadEx = null;
        var staThread = new Thread(() =>
        {
            try
            {
                var overlay = new SubtitleOverlayWindow();
                overlay.ShowActivated.Should().BeFalse();

                // 1. EnsureShown creates native HWND and makes window ready without stealing focus
                overlay.EnsureShown();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
                hwnd.Should().NotBe(nint.Zero);

                // 2. PositionNearRegion converts physical screen coordinates (e.g. 444, 806 at 125% DPI) and clamps to monitor work area
                var region = new ScreenRegion(444, 806, 458, 85);
                overlay.PositionNearRegion(region, ignoreLock: true);
                overlay.Left.Should().BeGreaterThanOrEqualTo(0);
                overlay.Top.Should().BeGreaterThanOrEqualTo(0);

                // 3. ShowTestSubtitle displays [TEST] diagnostic subtitle on Overlay
                overlay.ShowTestSubtitle();
                overlay.Visibility.Should().Be(System.Windows.Visibility.Visible);

                // 4. UpdateSubtitle with real translated subtitle keeps Overlay visible
                var sub = new SubtitleItem
                {
                    OriginalText = "山路一路蜿蜒，",
                    OriginalLanguageCode = "zh",
                    EnglishText = "The mountain roads twisted",
                    ChineseText = "山路一路蜿蜒，",
                    VietnameseText = "Đường núi xoay quanh",
                    DisplayMode = DisplayMode.Triple
                };
                overlay.UpdateSubtitle(sub);
                overlay.Visibility.Should().Be(System.Windows.Visibility.Visible);
                overlay.LastUpdateLatencyMs.Should().BeGreaterThanOrEqualTo(1);

                overlay.Close();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        threadEx.Should().BeNull();
    }

    [Fact]
    public async Task TestK_EnglishOcrWordSpacing_RealTightAndCompressedSubtitles_ShouldReconstructCleanlyWithoutBreakingBrandsOrCjkVi()
    {
        var cases = new (string FontName, float FontSize, bool OutlinedJpeg, string GroundTruth, SourceLanguage Lang)[]
        {
            ("Arial", 20f, false, "out it may belong to a different township.", SourceLanguage.English),
            ("Segoe UI", 18f, false, "Do not turn off the computer before leaving the office.", SourceLanguage.English),
            ("Arial", 20f, true,  "But it may belong to a different township.", SourceLanguage.English),
            ("Arial", 20f, false, "This is the first time I have seen YouTube on my iPhone.", SourceLanguage.English),
            ("Arial", 18f, false, "We must find out if it belongs to a different township.", SourceLanguage.English),
            ("Arial", 20f, true,  "Please check the server latency in the production cluster.", SourceLanguage.English),
            ("Microsoft YaHei", 22f, false, "你好，欢迎大家参加今天的软件测试会议。", SourceLanguage.Chinese)
        };

        foreach (var (fontName, fontSize, outlinedJpeg, gt, lang) in cases)
        {
            byte[] imgBytes = RenderSingleLineSubtitleScreenshot(gt, fontName, fontSize, outlinedJpeg);
            var ocrResult = await _ocrEngine.RecognizeAsync(imgBytes);
            ocrResult.HasText.Should().BeTrue($"OCR should recognize '{gt}'");

            var analysis = SubtitleTextProcessor.AnalyzeOcrText(ocrResult.FullText, lang);
            analysis.PrimarySourceText.Should().Be(gt,
                $"tight/compressed subtitle '{gt}' ({fontName} {fontSize}px, jpeg={outlinedJpeg}) must preserve word spacing without gluing words or breaking brands");
        }
    }

    private static byte[] RenderSingleLineSubtitleScreenshot(string text, string fontName, float fontSize, bool outlinedJpeg)
    {
        using var bmp = new Bitmap(920, 96);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(18, 22, 32));
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        using var font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var whiteBrush = new SolidBrush(Color.White);
        using var blackBrush = new SolidBrush(Color.Black);

        if (outlinedJpeg)
        {
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (1, 1) })
            {
                g.DrawString(text, font, blackBrush, 24 + dx, 32 + dy);
            }
        }
        g.DrawString(text, font, whiteBrush, 24, 32);

        using var ms = new MemoryStream();
        if (outlinedJpeg)
        {
            var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 62L);
            bmp.Save(ms, jpegCodec, encParams);
        }
        else
        {
            bmp.Save(ms, ImageFormat.Png);
        }
        return ms.ToArray();
    }
}
