using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using ScreenSub.Translation;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests;

public class RealWorkerMultiModelTests
{
    private readonly ITestOutputHelper _output;

    public RealWorkerMultiModelTests(ITestOutputHelper output)
    {
        _output = output;
    }
    private static string ResolveWorkerScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "workers", "translation", "translation_worker.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate workers/translation/translation_worker.py from test base directory.");
    }

    private static ISettingsService CreateSettingsService(AppSettings settings)
    {
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.Settings).Returns(settings);
        mock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);
        return mock.Object;
    }

    private static void CreateJunction(string linkPath, string targetPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new IOException($"mklink /J failed: {proc.StandardError.ReadToEnd()}");
        }
    }

    [Fact]
    public async Task RealWorker_MultiModelRouter_ShouldRouteAllSixDirectionsAccuratelyWithBoundedMemory()
    {
        LocalNeuralTranslationEngine.FindInstalledModelDirectory(LocalNeuralTranslationEngine.ModelIdM2M100).Should().NotBeNull();
        LocalNeuralTranslationEngine.FindInstalledModelDirectory(LocalNeuralTranslationEngine.ModelIdEnViT5).Should().NotBeNull();
        LocalNeuralTranslationEngine.FindInstalledModelDirectory(LocalNeuralTranslationEngine.ModelIdOpusZhVi).Should().NotBeNull();

        var settings = new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Offline,
            EnableOfflineMultiModelRouter = true,
            EnableExperimentalZhViOpusModel = true,
            AllowOfflineSpecializedFallbackToM2M100 = true,
            MaxLoadedTranslationModelsInMemory = 1
        };

        string workerPath = ResolveWorkerScriptPath();
        using var engine = new LocalNeuralTranslationEngine(CreateSettingsService(settings), "python", workerPath)
        {
            SkipStartupAutoLoad = true
        };

        await engine.InitializeAsync();
        engine.State.Should().Be(ModelRuntimeState.Ready);

        // 1. EN -> VI => EnViT5
        var enVi = await engine.TranslateTextDetailedAsync("We're just having a drink.", "en", "vi");
        enVi.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);
        enVi.FallbackUsed.Should().BeFalse();
        enVi.TranslatedText.Should().NotStartWith("vi:");
        enVi.TranslatedText.ToLowerInvariant().Should().Contain("uống");

        // 2. VI -> EN => EnViT5 (warm reuse -> load=0)
        var viEn = await engine.TranslateTextDetailedAsync("Xin chào, rất vui được gặp bạn!", "vi", "en");
        viEn.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);
        viEn.ModelLoadMs.Should().Be(0);
        viEn.TranslatedText.Should().NotStartWith("en:");
        viEn.TranslatedText.ToLowerInvariant().Should().Contain("meet you");

        // 3. ZH (Traditional!) -> VI => Opus-MT Zh-Vi (verify LRU evicted EnViT5 so loaded == 1)
        var zhVi = await engine.TranslateTextDetailedAsync("太誇張了吧，排隊排了兩個小時還沒輪到我們。", "zh", "vi");
        zhVi.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdOpusZhVi);
        zhVi.TranslatedText.ToLowerInvariant().Should().Match(s => s.Contains("2 tiếng") || s.Contains("hai tiếng"));

        using (var regDoc = await engine.GetWorkerRegistryAsync())
        {
            var loaded = regDoc.RootElement.GetProperty("loaded_models").EnumerateArray().Select(e => e.GetString()).ToList();
            loaded.Should().Equal(LocalNeuralTranslationEngine.ModelIdOpusZhVi);
        }

        // 4. VI -> ZH => M2M-100
        var viZh = await engine.TranslateTextDetailedAsync("Hôm nay bạn có rảnh không?", "vi", "zh");
        viZh.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
        viZh.TranslatedText.Should().Contain("今天");

        // 5. EN -> ZH => M2M-100
        var enZh = await engine.TranslateTextDetailedAsync("What are you doing tonight?", "en", "zh");
        enZh.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
        enZh.TranslatedText.Should().NotBeNullOrWhiteSpace();

        // 6. ZH -> EN => M2M-100
        var zhEn = await engine.TranslateTextDetailedAsync("今天晚上你在做什么？", "zh", "en");
        zhEn.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
        zhEn.TranslatedText.ToLowerInvariant().Should().Contain("tonight");

        using (var regDoc = await engine.GetWorkerRegistryAsync())
        {
            var loaded = regDoc.RootElement.GetProperty("loaded_models").EnumerateArray().Select(e => e.GetString()).ToList();
            loaded.Should().Equal(LocalNeuralTranslationEngine.ModelIdM2M100);
        }

        engine.WorkerCrashOrResetCount.Should().Be(0);
    }

    [Fact]
    public async Task RealWorker_RouterToggle_ShouldIsolateCacheAcrossModelSwitches()
    {
        var settings = new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Offline,
            EnableOfflineMultiModelRouter = false, // Start with M2M-100 baseline
            MaxLoadedTranslationModelsInMemory = 1
        };
        var settingsSvc = CreateSettingsService(settings);
        var cache = new TranslationCache();
        using var engine = new LocalNeuralTranslationEngine(settingsSvc, "python", ResolveWorkerScriptPath());
        await engine.InitializeAsync();
        using var localProvider = new LocalTranslationProvider(engine, cache, settingsSvc);

        const string sample = "We're just having a drink.";

        // Call 1: Router OFF -> M2M-100
        var resBaseline = await localProvider.TranslateAsync(sample, SourceLanguage.English, [TargetLanguage.Vietnamese]);
        resBaseline.IsFromCache.Should().BeFalse();
        resBaseline.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);

        // Switch ON router dynamically on live provider + shared cache
        settings.EnableOfflineMultiModelRouter = true;

        // Call 2: Router ON -> MUST miss M2M-100 cache and invoke EnViT5
        var resRouter = await localProvider.TranslateAsync(sample, SourceLanguage.English, [TargetLanguage.Vietnamese]);
        resRouter.IsFromCache.Should().BeFalse();
        resRouter.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);
        resRouter.Translations[TargetLanguage.Vietnamese].Should().NotBe(resBaseline.Translations[TargetLanguage.Vietnamese]);

        // Call 3: Router ON repeat -> MUST hit EnViT5 cache
        var resRouterCached = await localProvider.TranslateAsync(sample, SourceLanguage.English, [TargetLanguage.Vietnamese]);
        resRouterCached.IsFromCache.Should().BeTrue();
        resRouterCached.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);

        // Call 4: Rollback Router OFF -> MUST hit M2M-100 baseline cache rather than returning EnViT5
        settings.EnableOfflineMultiModelRouter = false;
        var resRollbackCached = await localProvider.TranslateAsync(sample, SourceLanguage.English, [TargetLanguage.Vietnamese]);
        resRollbackCached.IsFromCache.Should().BeTrue();
        resRollbackCached.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
    }

    [Fact]
    public async Task RealWorker_MissingSpecializedModel_ShouldRespectFallbackPolicyWithoutCloudFallback()
    {
        string m2mSourceDir = LocalNeuralTranslationEngine.FindInstalledModelDirectory(LocalNeuralTranslationEngine.ModelIdM2M100)!;
        string isolatedRoot = Path.Combine(Path.GetTempPath(), $"screensub_m2m_only_{Guid.NewGuid():N}");
        string junctionDir = Path.Combine(isolatedRoot, "m2m100_418m_ct2");
        Directory.CreateDirectory(isolatedRoot);

        try
        {
            CreateJunction(junctionDir, m2mSourceDir);

            var settings = new AppSettings
            {
                TranslationEngineMode = TranslationEngineMode.Offline,
                AllowCloudFallbackOnOfflineFailure = false,
                EnableOfflineMultiModelRouter = true,
                AllowOfflineSpecializedFallbackToM2M100 = true,
                MaxLoadedTranslationModelsInMemory = 1
            };
            var settingsSvc = CreateSettingsService(settings);
            var cache = new TranslationCache();

            using var engine = new LocalNeuralTranslationEngine(settingsSvc, "python", ResolveWorkerScriptPath())
            {
                CustomModelsRoot = isolatedRoot,
                SkipStartupAutoLoad = true
            };
            await engine.InitializeAsync();
            using var localProvider = new LocalTranslationProvider(engine, cache, settingsSvc);
            var azureProvider = new AzureTranslatorProvider(new HttpClient(), settingsSvc, cache);
            var mockProvider = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azureProvider, mockProvider);

            // Policy A: Fallback enabled -> missing EnViT5 falls back to offline M2M-100
            var fallbackRes = await resolver.TranslateAsync(
                "Please wait a moment.",
                SourceLanguage.English,
                [TargetLanguage.Vietnamese]);
            fallbackRes.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
            fallbackRes.UsedFallbackModel.Should().BeTrue();
            fallbackRes.Translations[TargetLanguage.Vietnamese].Should().NotBeNullOrWhiteSpace();

            // Policy B: Fallback disabled -> missing EnViT5 throws MODEL NOT INSTALLED (never falls back to cloud or mock)
            settings.AllowOfflineSpecializedFallbackToM2M100 = false;
            await engine.ConfigureWorkerRouterAsync();

            Func<Task> strictAct = async () => await resolver.TranslateAsync(
                "Thank you so much.",
                SourceLanguage.English,
                [TargetLanguage.Vietnamese]);

            await strictAct.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*MODEL NOT INSTALLED*");
        }
        finally
        {
            // Carefully unlink ONLY the junction reparse point before removing isolatedRoot (never recurse into M2M100 weights!)
            if (Directory.Exists(junctionDir))
            {
                Directory.Delete(junctionDir, false);
            }
            if (Directory.Exists(isolatedRoot))
            {
                Directory.Delete(isolatedRoot, true);
            }
        }
    }

    [Fact]
    public async Task RealWorker_RapidMultilingualSwitchingUnderOcrBurstAndRestart_ShouldStayBoundedAndRejectStale()
    {
        var settings = new AppSettings
        {
            CaptureFps = 10,
            TranslationMode = TranslationMode.ScreenOcr,
            TranslationEngineMode = TranslationEngineMode.Offline,
            SourceLanguage = SourceLanguage.Auto,
            TargetLanguages = [TargetLanguage.Vietnamese],
            DisplayMode = DisplayMode.Triple,
            EnableContextAwareTranslation = false,
            EnableOfflineMultiModelRouter = true,
            EnableExperimentalZhViOpusModel = true,
            MaxLoadedTranslationModelsInMemory = 1,
            LastRegion = new ScreenRegion(100, 100, 600, 120)
        };
        var settingsSvc = CreateSettingsService(settings);
        var cache = new TranslationCache();

        using var engine = new LocalNeuralTranslationEngine(settingsSvc, "python", ResolveWorkerScriptPath())
        {
            SkipStartupAutoLoad = true
        };
        await engine.InitializeAsync();
        using var localProvider = new LocalTranslationProvider(engine, cache, settingsSvc);

        string activeOcrText = "Hold on, I'll be right back.";
        int frameCounter = 0;
        var captureMock = new Mock<IScreenCaptureService>();
        captureMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                It.IsAny<ScreenRegion>(),
                It.IsAny<WindowInfo?>(),
                It.IsAny<RegionMode>(),
                It.IsAny<CaptureSourceType>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int id = Interlocked.Increment(ref frameCounter);
                return new CaptureFrameResult
                {
                    ImageBytes = [(byte)(id & 0xFF), 1, 2, 3],
                    ErrorKind = CaptureErrorKind.None,
                    CaptureLatencyMs = 1
                };
            });

        var changeDetectorMock = new Mock<IFrameChangeDetector>();
        changeDetectorMock.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>())).Returns(true);

        var enDispatched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Connected);
        ocrMock.Setup(o => o.StatusMessage).Returns("Ready");
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                string txt = Volatile.Read(ref activeOcrText);
                if (txt.StartsWith("Hold on", StringComparison.Ordinal))
                {
                    enDispatched.TrySetResult(true);
                }
                return new OcrResult { FullText = txt, Confidence = 0.98, LatencyMs = 1 };
            });

        var resolverMock = new Mock<ITranslationProviderResolver>();
        resolverMock.Setup(r => r.ResolveActiveProvider()).Returns(localProvider);
        resolverMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(localProvider);
        resolverMock.Setup(r => r.ActiveProviderBadgeText).Returns("OFFLINE ROUTER");

        await using var coordinator = new SubtitleCoordinator(
            captureMock.Object,
            ocrMock.Object,
            resolverMock.Object,
            settingsSvc,
            changeDetectorMock.Object);

        int phase = 1;
        var postRestartItems = new ConcurrentQueue<SubtitleItem>();
        var zhWinnerTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (Volatile.Read(ref phase) == 2)
            {
                postRestartItems.Enqueue(item);
                if (item.OriginalText == "你确定现在就要离开吗？")
                {
                    zhWinnerTcs.TrySetResult(item);
                }
            }
        };

        // Session 1: Start on EN subtitle, switch quickly during OCR burst, then Stop mid-session
        coordinator.Start();
        await enDispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Volatile.Write(ref activeOcrText, "I need to grab my keys first.");
        await Task.Delay(120);
        coordinator.Stop();

        // Session 2: Restart immediately with ZH -> VI subtitle (switches model EnViT5 -> Opus-MT)
        Volatile.Write(ref phase, 2);
        Volatile.Write(ref activeOcrText, "你确定现在就要离开吗？");
        coordinator.Start();

        var winner = await zhWinnerTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        coordinator.Stop();

        winner.VietnameseText.Should().NotBeNullOrWhiteSpace();
        postRestartItems.Should().OnlyContain(i =>
            i.OriginalText != "Hold on, I'll be right back." &&
            i.OriginalText != "I need to grab my keys first.");
        coordinator.OcrQueueLength.Should().BeLessThanOrEqualTo(1);
        engine.WorkerCrashOrResetCount.Should().Be(0);
    }

    [Fact]
    public async Task RealWorker_EndToEnd_CoordinatorLatencyAndCacheBenchmark_ShouldMeetRealtimeTargets()
    {
        var settings = new AppSettings
        {
            CaptureFps = 10,
            TranslationMode = TranslationMode.ScreenOcr,
            TranslationEngineMode = TranslationEngineMode.Offline,
            SourceLanguage = SourceLanguage.Auto,
            TargetLanguages = [TargetLanguage.Vietnamese],
            DisplayMode = DisplayMode.Triple,
            EnableContextAwareTranslation = false,
            EnableOfflineMultiModelRouter = true,
            EnableExperimentalZhViOpusModel = true,
            MaxLoadedTranslationModelsInMemory = 1,
            LastRegion = new ScreenRegion(100, 100, 600, 120)
        };
        var settingsSvc = CreateSettingsService(settings);
        var cache = new TranslationCache();

        using var engine = new LocalNeuralTranslationEngine(settingsSvc, "python", ResolveWorkerScriptPath())
        {
            SkipStartupAutoLoad = true
        };
        await engine.InitializeAsync();
        using var localProvider = new LocalTranslationProvider(engine, cache, settingsSvc);

        // 1. Measure Cache Miss vs Cache Hit directly at provider layer
        var swMiss = Stopwatch.StartNew();
        var missRes = await localProvider.TranslateAsync("We're just having a drink.", SourceLanguage.English, [TargetLanguage.Vietnamese]);
        swMiss.Stop();

        var swHit = Stopwatch.StartNew();
        var hitRes = await localProvider.TranslateAsync("We're just having a drink.", SourceLanguage.English, [TargetLanguage.Vietnamese]);
        swHit.Stop();

        missRes.IsFromCache.Should().BeFalse();
        hitRes.IsFromCache.Should().BeTrue();
        hitRes.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);

        // 2. Measure E2E SubtitleCoordinator latency across EN -> ZH alternating sequence (10 warm subtitles + switches)
        var testLines = new[]
        {
            "Let's grab a coffee and catch up.",
            "That rings a bell, doesn't it?",
            "Fair enough.",
            "You're kidding, right?",
            "I couldn't care less.",
            "Stop beating around the bush and tell me straight.",
            "We're running out of time.",
            "Why didn't you say something earlier?!",
            "太誇張了吧，排隊排了兩個小時還沒輪到我們。",
            "先別急著下結論，聽他把話說完。"
        };

        int currentIdx = 0;
        long frameCounter = 0;
        var currentPublishedTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedLatencies = new List<double>();
        var captureMock = new Mock<IScreenCaptureService>();
        captureMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                It.IsAny<ScreenRegion>(), It.IsAny<WindowInfo?>(), It.IsAny<RegionMode>(), It.IsAny<CaptureSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                long id = Interlocked.Increment(ref frameCounter);
                return new CaptureFrameResult
                {
                    ImageBytes = [(byte)(id & 0xFF), (byte)((id >> 8) & 0xFF), 5, 6],
                    ErrorKind = CaptureErrorKind.None,
                    CaptureLatencyMs = 1
                };
            });

        var changeDetectorMock = new Mock<IFrameChangeDetector>();
        changeDetectorMock.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>())).Returns(true);

        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Connected);
        ocrMock.Setup(o => o.StatusMessage).Returns("Ready");
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OcrResult
            {
                FullText = testLines[Volatile.Read(ref currentIdx)],
                Confidence = 0.99,
                LatencyMs = 1
            });

        var resolverMock = new Mock<ITranslationProviderResolver>();
        resolverMock.Setup(r => r.ResolveActiveProvider()).Returns(localProvider);
        resolverMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(localProvider);
        resolverMock.Setup(r => r.ActiveProviderBadgeText).Returns("OFFLINE ROUTER");

        await using var coordinator = new SubtitleCoordinator(
            captureMock.Object,
            ocrMock.Object,
            resolverMock.Object,
            settingsSvc,
            changeDetectorMock.Object);

        string expectedText = testLines[0];
        Stopwatch itemSw = Stopwatch.StartNew();
        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (item.OriginalText == Volatile.Read(ref expectedText))
            {
                currentPublishedTcs.TrySetResult(itemSw.ElapsedMilliseconds);
            }
        };

        coordinator.Start();
        for (int i = 0; i < testLines.Length; i++)
        {
            currentPublishedTcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref expectedText, testLines[i]);
            Volatile.Write(ref currentIdx, i);
            itemSw.Restart();
            long elapsed = await currentPublishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            publishedLatencies.Add(elapsed);
        }
        coordinator.Stop();

        var warmE2E = publishedLatencies.Skip(1).Take(7).OrderBy(x => x).ToList(); // EN warm subset
        double e2eP50 = warmE2E[warmE2E.Count / 2];
        double e2eP95 = warmE2E[(int)Math.Round(0.95 * (warmE2E.Count - 1))];
        double switchZhMs = publishedLatencies[8]; // EN -> ZH cold switch
        double warmZhMs = publishedLatencies[9];   // ZH warm

        _output.WriteLine(
            $"[E2E_BENCHMARK] cold_miss_ms={swMiss.ElapsedMilliseconds}ms, cache_hit_ms={swHit.ElapsedMilliseconds}ms, " +
            $"en_warm_p50={e2eP50}ms, en_warm_p95={e2eP95}ms, switch_en_to_zh_ms={switchZhMs}ms, zh_warm_ms={warmZhMs}ms, " +
            $"dropped={coordinator.DroppedObsoleteCount}, stale={coordinator.RejectedStaleCount}, crashes={engine.WorkerCrashOrResetCount}");

        hitRes.LatencyMs.Should().Be(0);
        e2eP50.Should().BeLessThan(800);
        engine.WorkerCrashOrResetCount.Should().Be(0);
    }
}
