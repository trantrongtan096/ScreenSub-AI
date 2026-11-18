using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using ScreenSub.Ocr;
using ScreenSub.Translation;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests;

public class Phase9OfflinePipelineIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private PaddleOcrEngine _ocrEngine = null!;
    private LocalNeuralTranslationEngine _neuralEngine = null!;

    public Phase9OfflinePipelineIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        _ocrEngine = new PaddleOcrEngine();
        await _ocrEngine.InitializeAsync();

        _neuralEngine = new LocalNeuralTranslationEngine();
        await _neuralEngine.InitializeAsync(null, InferenceBackendType.Cpu);
    }

    public async Task DisposeAsync()
    {
        await _ocrEngine.DisposeAsync();
        _neuralEngine.Dispose();
    }

    private static string GetAssetPath(string fileName)
    {
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
    public async Task FullOfflinePipeline_RealImage_OcrAndOfflineTranslation_ShouldSucceedWithoutInternet()
    {
        // 1. Load real Chinese subtitle frame
        string imgPath = GetAssetPath("chinese_subtitle.png");
        byte[] imageBytes = await File.ReadAllBytesAsync(imgPath);

        // 2. Perform OCR locally
        var ocrResult = await _ocrEngine.RecognizeAsync(imageBytes);
        ocrResult.HasText.Should().BeTrue();
        ocrResult.FullText.Should().Contain("你今天有时间吗？");

        // 3. Perform Offline Translation locally (0 network calls)
        var cache = new TranslationCache();
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Offline,
            AllowCloudFallbackOnOfflineFailure = false
        });

        var localProvider = new LocalTranslationProvider(_neuralEngine, cache, mockSettings.Object);
        localProvider.IsOffline.Should().BeTrue();

        var targets = new List<TargetLanguage> { TargetLanguage.English, TargetLanguage.Vietnamese };
        var transResult = await localProvider.TranslateAsync(ocrResult.FullText, SourceLanguage.Chinese, targets);

        transResult.Should().NotBeNull();
        transResult.DetectedLanguage.Should().Be("zh");
        transResult.Translations[TargetLanguage.English].Should().ContainAny("free", "time", "today");
        transResult.Translations[TargetLanguage.Vietnamese].Should().ContainAny("hôm nay", "thời gian", "rảnh", "bạn", "anh");
    }

    [Fact]
    public async Task FullOfflinePipeline_EnglishSubtitles_ShouldTranslateToVietnameseAndChinese()
    {
        string imgPath = GetAssetPath("english_subtitle.png");
        byte[] imageBytes = await File.ReadAllBytesAsync(imgPath);

        var ocrResult = await _ocrEngine.RecognizeAsync(imageBytes);
        ocrResult.HasText.Should().BeTrue();

        var cache = new TranslationCache();
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());

        var localProvider = new LocalTranslationProvider(_neuralEngine, cache, mockSettings.Object);
        var targets = new List<TargetLanguage> { TargetLanguage.Vietnamese, TargetLanguage.Chinese };
        var transResult = await localProvider.TranslateAsync(ocrResult.FullText, SourceLanguage.English, targets);

        transResult.Should().NotBeNull();
        transResult.Translations[TargetLanguage.Vietnamese].Should().NotBeNullOrWhiteSpace();
        transResult.Translations[TargetLanguage.Chinese].Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Benchmark_MeasureExactLatenciesAndResourceConsumption()
    {
        string imgPath = GetAssetPath("chinese_subtitle.png");
        byte[] imageBytes = await File.ReadAllBytesAsync(imgPath);

        // 1. OCR Latencies (Cold start + 5 iterations)
        var ocrLatencies = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var ocr = await _ocrEngine.RecognizeAsync(imageBytes);
            sw.Stop();
            ocrLatencies.Add(sw.ElapsedMilliseconds);
            ocr.HasText.Should().BeTrue();
        }

        // 2. Translation Latencies (Cold start + cache hit)
        var cache = new TranslationCache();
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());
        var localProvider = new LocalTranslationProvider(_neuralEngine, cache, mockSettings.Object);
        var targets = new List<TargetLanguage> { TargetLanguage.English, TargetLanguage.Vietnamese };

        var transLatencies = new List<long>();
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await localProvider.TranslateAsync("你今天有时间吗？", SourceLanguage.Chinese, targets);
            sw.Stop();
            transLatencies.Add(sw.ElapsedMilliseconds);
        }

        long memBytes = GC.GetTotalMemory(true);

        _output.WriteLine("=== BENCHMARK REPORT ===");
        _output.WriteLine($"OCR Latencies (5 runs): {string.Join(", ", ocrLatencies)} ms (P50: {ocrLatencies.OrderBy(x => x).ElementAt(2)} ms)");
        _output.WriteLine($"Local Translation Latencies (5 runs): {string.Join(", ", transLatencies)} ms (Run 1: {transLatencies[0]} ms, Cached: {transLatencies[1]} ms)");
        _output.WriteLine($"Process Managed Memory: {memBytes / (1024 * 1024)} MB");
    }

    [Fact]
    public async Task UnseenSentences_RealNeuralInference_ShouldTranslateAccuratelyAcrossSixDirections()
    {
        // Verify real neural model is loaded
        _neuralEngine.IsNeuralModelLoaded.Should().BeTrue();
        _neuralEngine.ActiveModelName.Should().Contain("M2M-100");

        // 1. EN -> VI (unseen)
        string enText = "The weather is very pleasant this morning.";
        string viRes = await _neuralEngine.TranslateTextAsync(enText, "en", "vi");
        _output.WriteLine($"[EN -> VI] '{enText}' => '{viRes}'");
        viRes.Should().NotBeNullOrWhiteSpace();
        viRes.Should().NotContain("[MODEL NOT INSTALLED]");
        viRes.Should().NotContain("[LEXICAL");

        // 2. VI -> EN (unseen)
        string viText = "Tôi muốn đặt một bàn cho hai người vào tối nay.";
        string enRes = await _neuralEngine.TranslateTextAsync(viText, "vi", "en");
        _output.WriteLine($"[VI -> EN] '{viText}' => '{enRes}'");
        enRes.Should().NotBeNullOrWhiteSpace();
        enRes.ToLowerInvariant().Should().ContainAny("table", "book", "two", "tonight");

        // 3. ZH -> VI (unseen)
        string zhText = "这个电影的情节非常感人。";
        string zhViRes = await _neuralEngine.TranslateTextAsync(zhText, "zh", "vi");
        _output.WriteLine($"[ZH -> VI] '{zhText}' => '{zhViRes}'");
        zhViRes.Should().NotBeNullOrWhiteSpace();
        zhViRes.ToLowerInvariant().Should().ContainAny("phim", "cảm động", "cốt truyện", "tình tiết");

        // 4. VI -> ZH (unseen)
        string viZhText = "Tôi hy vọng ngày mai trời sẽ nắng đẹp.";
        string viZhRes = await _neuralEngine.TranslateTextAsync(viZhText, "vi", "zh");
        _output.WriteLine($"[VI -> ZH] '{viZhText}' => '{viZhRes}'");
        viZhRes.Should().NotBeNullOrWhiteSpace();
        viZhRes.Should().ContainAny("明天", "天气", "希望", "晴");

        // 5. EN -> ZH (unseen)
        string enZhText = "Can you please tell me how to get to the train station?";
        string enZhRes = await _neuralEngine.TranslateTextAsync(enZhText, "en", "zh");
        _output.WriteLine($"[EN -> ZH] '{enZhText}' => '{enZhRes}'");
        enZhRes.Should().NotBeNullOrWhiteSpace();
        enZhRes.Should().ContainAny("火车站", "车站", "怎么", "请问");

        // 6. ZH -> EN (unseen)
        string zhEnText = "我们必须在日落之前完成这项工作。";
        string zhEnRes = await _neuralEngine.TranslateTextAsync(zhEnText, "zh", "en");
        _output.WriteLine($"[ZH -> EN] '{zhEnText}' => '{zhEnRes}'");
        zhEnRes.Should().NotBeNullOrWhiteSpace();
        zhEnRes.ToLowerInvariant().Should().ContainAny("sunset", "finish", "complete", "work", "must");
    }
}

