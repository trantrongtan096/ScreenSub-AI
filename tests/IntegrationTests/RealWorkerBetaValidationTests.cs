using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ScreenSub.Audio.VAD;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using ScreenSub.Ocr;
using ScreenSub.Translation;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests;

public class RealWorkerBetaValidationTests
{
    private readonly ITestOutputHelper _output;

    public RealWorkerBetaValidationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string ResolveRepoRelative(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var probe = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(probe) || Directory.Exists(probe))
            {
                return probe;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate repo path: {Path.Combine(parts)}");
    }

    private static string ResolveTestAsset(string fileName)
    {
        string local = Path.Combine(AppContext.BaseDirectory, "test_assets", fileName);
        return File.Exists(local) ? local : ResolveRepoRelative("test_assets", fileName);
    }

    private static async Task<Dictionary<string, byte[]>> RenderSubtitleFramesAsync(string tempDir, Dictionary<string, string> keyToText)
    {
        string specPath = Path.Combine(tempDir, "render_spec.json");
        string outDir = Path.Combine(tempDir, "frames");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(specPath, System.Text.Json.JsonSerializer.Serialize(keyToText));

        string pyScript = Path.Combine(tempDir, "render_subs.py");
        string py = @"
import json, pathlib, sys
from PIL import Image, ImageDraw, ImageFont

spec = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8'))
out_dir = pathlib.Path(sys.argv[2])

def pick_font(text):
    has_cjk = any('\u4e00' <= c <= '\u9fff' for c in text)
    candidates = [
        r'C:\Windows\Fonts\msyh.ttc',
        r'C:\Windows\Fonts\simhei.ttf',
        r'C:\Windows\Fonts\segoeui.ttf',
        r'C:\Windows\Fonts\arial.ttf',
    ] if has_cjk else [
        r'C:\Windows\Fonts\arialbd.ttf',
        r'C:\Windows\Fonts\arial.ttf',
        r'C:\Windows\Fonts\segoeui.ttf',
        r'C:\Windows\Fonts\msyh.ttc',
    ]
    for c in candidates:
        if pathlib.Path(c).exists():
            return ImageFont.truetype(c, 34)
    return ImageFont.load_default()

for key, text in spec.items():
    img = Image.new('RGB', (1280, 180), color=(18, 22, 28))
    draw = ImageDraw.Draw(img)
    font = pick_font(text)
    draw.multiline_text((48, 36), text, font=font, fill=(255, 255, 255), spacing=12)
    img.save(out_dir / f'{key}.png', format='PNG')
";
        await File.WriteAllTextAsync(pyScript, py);

        var psi = new ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{pyScript}\" \"{specPath}\" \"{outDir}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to render test PNG frames: {err}");
        }

        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in keyToText.Keys)
        {
            result[key] = await File.ReadAllBytesAsync(Path.Combine(outDir, $"{key}.png"));
        }
        return result;
    }

    private static (double TotalRssMb, double TotalCpuSec, int Count) MeasureRecentPythonProcesses(DateTime utcSince)
    {
        double rssMb = 0;
        double cpuSec = 0;
        int count = 0;
        foreach (var proc in Process.GetProcessesByName("python"))
        {
            try
            {
                if (proc.StartTime.ToUniversalTime() >= utcSince.AddSeconds(-1))
                {
                    proc.Refresh();
                    rssMb += proc.WorkingSet64 / (1024.0 * 1024.0);
                    cpuSec += proc.TotalProcessorTime.TotalSeconds;
                    count++;
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
        return (rssMb, cpuSec, count);
    }

    [Fact]
    public async Task Phase2And4_BetaEnViProfile_AllDirectionsCoResidentMemoryAndRollback()
    {
        DateTime startUtc = DateTime.UtcNow;
        string workerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
        var betaSettings = AppSettings.CreateBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);

        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(betaSettings);

        using var engine = new LocalNeuralTranslationEngine("python", workerPath)
        {
            EnableMultiModelRouter = betaSettings.EnableOfflineMultiModelRouter,
            EnableExperimentalZhViOpusModel = betaSettings.EnableExperimentalZhViOpusModel,
            AllowSpecializedFallbackToM2M100 = betaSettings.AllowOfflineSpecializedFallbackToM2M100,
            MaxLoadedModelsInMemory = betaSettings.MaxLoadedTranslationModelsInMemory,
            AllowLexicalFallback = false
        };

        await engine.InitializeAsync();
        var initMem = MeasureRecentPythonProcesses(startUtc);

        // 1. EN -> VI routes to EnViT5
        var enVi = await engine.TranslateTextDetailedAsync("Do not unplug the cable while updating.", "en", "vi");
        enVi.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);
        enVi.DecodingConfig.Should().Contain("beam=1");
        enVi.TranslatedText.Should().NotBeNullOrWhiteSpace();
        var afterEnViMem = MeasureRecentPythonProcesses(startUtc);

        // 2. VI -> EN routes to EnViT5
        var viEn = await engine.TranslateTextDetailedAsync("Hẹn gặp mọi người lúc 8 giờ tối nhé.", "vi", "en");
        viEn.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);
        viEn.DecodingConfig.Should().Contain("beam=2");

        // 3. ZH -> VI keeps M2M-100 when EnableExperimentalZhViOpusModel = false
        var zhVi = await engine.TranslateTextDetailedAsync("先别急着下结论，等检测报告出来再说。", "zh", "vi");
        zhVi.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
        zhVi.DecodingConfig.Should().Contain("beam=2");
        var afterBothMem = MeasureRecentPythonProcesses(startUtc);

        // 4. Co-resident check: alternating EN->VI then ZH->VI with cap=2 should have ZERO cold reload!
        var enViWarmAgain = await engine.TranslateTextDetailedAsync("Grab a booth by the window.", "en", "vi");
        var zhViWarmAgain = await engine.TranslateTextDetailedAsync("我刚才重启了一下路由器。", "zh", "vi");
        enViWarmAgain.ModelLoadMs.Should().Be(0);
        zhViWarmAgain.ModelLoadMs.Should().Be(0);
        enViWarmAgain.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdEnViT5);
        zhViWarmAgain.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);

        // 5. Remaining directions stay on M2M-100
        var enZh = await engine.TranslateTextDetailedAsync("Wait for me outside.", "en", "zh");
        var viZh = await engine.TranslateTextDetailedAsync("Đợi tôi ở bên ngoài nhé.", "vi", "zh");
        var zhEn = await engine.TranslateTextDetailedAsync("请稍等一下。", "zh", "en");
        enZh.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
        viZh.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
        zhEn.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);

        // 6. Strict Rollback via AppSettings & Cache Identity invalidation
        var cache = new TranslationCache();
        string betaCacheId = engine.GetCacheIdentity("en", "vi");
        cache.Set("Hold your horses.", SourceLanguage.English, new[] { TargetLanguage.Vietnamese }, new TranslationResult
        {
            OriginalText = "Hold your horses.",
            Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = enViWarmAgain.TranslatedText }
        }, cacheIdentity: betaCacheId);

        betaSettings.ApplyLegacyM2M100RollbackProfile();
        engine.EnableMultiModelRouter = betaSettings.EnableOfflineMultiModelRouter;
        engine.MaxLoadedModelsInMemory = betaSettings.MaxLoadedTranslationModelsInMemory;
        string legacyCacheId = engine.GetCacheIdentity("en", "vi");
        legacyCacheId.Should().NotBe(betaCacheId);
        cache.TryGet("Hold your horses.", SourceLanguage.English, new[] { TargetLanguage.Vietnamese }, out _, cacheIdentity: legacyCacheId).Should().BeFalse();

        var rolledBack = await engine.TranslateTextDetailedAsync("Hold your horses.", "en", "vi");
        rolledBack.ModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);

        _output.WriteLine(
            $"[BETA_CO_RESIDENT] procCount={afterBothMem.Count}, initRss={initMem.TotalRssMb:F1}MB, " +
            $"afterEnViRss={afterEnViMem.TotalRssMb:F1}MB (loadMs={enVi.ModelLoadMs:F0}), " +
            $"afterEnVi+M2M100Rss={afterBothMem.TotalRssMb:F1}MB (m2mLoadMs={zhVi.ModelLoadMs:F0}), " +
            $"totalCpuSec={afterBothMem.TotalCpuSec:F2}s, crashes={engine.WorkerCrashOrResetCount}");
    }

    [Fact]
    public async Task Phase4_RealOcrAndBetaRouter_Scenarios1To10_WithTelemetryAndSoak()
    {
        DateTime startUtc = DateTime.UtcNow;
        string tempDir = Path.Combine(Path.GetTempPath(), $"ScreenSubBetaP4_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
        string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");

        var frameSpec = new Dictionary<string, string>
        {
            ["en_01"] = "Don't tell me you forgot the key.",
            ["en_02"] = "Press Ctrl+Shift+S to capture again.",
            ["en_03"] = "As soon as Mia lands, grab the badges.",
            ["multi_01"] = "I wouldn't say she isn't qualified,\nbut she needs more fieldwork experience.",
            ["burst_a"] = "First frame dialogue quickly replaced.",
            ["burst_b"] = "Second intermediate frame discarded.",
            ["burst_final"] = "Wait until the crowd clears the tunnel.",
            ["zh_01"] = "先别急着下结论，等报告出来再说。",
            ["resume_01"] = "Clean recovery right after pause and stop.",
            ["quick_01"] = "Quick translate switched region cleanly.",
            ["hybrid_ocr"] = "Keep latency under 250 ms at P95."
        };
        string[] soakSentences =
        [
            "Grab the spare tripod before the rain starts.",
            "Nobody noticed that the server rack went silent.",
            "Meet Dr. Nguyen outside Ben Thanh Market at noon.",
            "Press Ctrl+Alt+T to move the overlay down.",
            "Don't leave the prototype unplugged overnight again.",
            "The ferry resumes at six unless winds pick up.",
            "Ask Claire to forward the updated Figma link.",
            "Room 309 costs 125 dollars per night plus tax.",
            "She handed me an unlabeled flash drive upstairs.",
            "Keep latency under 250 ms on an 8 GB laptop.",
            "Whenever the AC starts, the mic hums faintly.",
            "You can't ignore a client email copied to the boss.",
            "Export at 60 fps and keep audio above 192 kbps.",
            "Traffic crawls whenever evening fireworks begin.",
            "Wait until the platform clears before rolling cameras.",
            "Even after cleaning the heatsink, temps hit 32°C.",
            "First verify every SHA-256 hash on the local drive.",
            "Do not power off while firmware flashes sector zero.",
            "I promised to stay neutral during the salary debate.",
            "Run the database migration locally before merging.",
            "Hardly anyone arrived before the storm finally stopped.",
            "Remember to tag Victor and Linh on GitHub PR #482.",
            "They promised overnight shipping across three islands.",
            "Hold your horses and double-check the release tag."
        ];
        for (int i = 0; i < soakSentences.Length; i++)
        {
            frameSpec[$"soak_{i:00}"] = soakSentences[i];
        }

        byte[] zhRealAsset = await File.ReadAllBytesAsync(ResolveTestAsset("chinese_subtitle.png"));
        byte[] enRealAsset = await File.ReadAllBytesAsync(ResolveTestAsset("english_subtitle.png"));
        var renderedFrames = await RenderSubtitleFramesAsync(tempDir, frameSpec);

        var betaSettings = AppSettings.CreateBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
        betaSettings.TargetLanguages = [TargetLanguage.Vietnamese];
        betaSettings.TranslationEngineMode = TranslationEngineMode.Offline;
        betaSettings.StabilityDebounceMs = 40;
        betaSettings.CaptureFps = 20;

        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(betaSettings);

        using var neuralEngine = new LocalNeuralTranslationEngine("python", transWorkerPath)
        {
            EnableMultiModelRouter = betaSettings.EnableOfflineMultiModelRouter,
            EnableExperimentalZhViOpusModel = betaSettings.EnableExperimentalZhViOpusModel,
            AllowSpecializedFallbackToM2M100 = betaSettings.AllowOfflineSpecializedFallbackToM2M100,
            MaxLoadedModelsInMemory = betaSettings.MaxLoadedTranslationModelsInMemory,
            AllowLexicalFallback = false
        };
        var localProvider = new LocalTranslationProvider(neuralEngine, new TranslationCache(), settingsMock.Object);

        await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
        await ocrEngine.InitializeAsync();
        ocrEngine.IsPaddleAvailable.Should().BeTrue();
        await neuralEngine.InitializeAsync();

        var resolverMock = new Mock<ITranslationProviderResolver>();
        resolverMock.Setup(r => r.ResolveActiveProvider()).Returns(localProvider);
        resolverMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(localProvider);
        resolverMock.Setup(r => r.ActiveProviderBadgeText).Returns("BETA OFFLINE ROUTER");

        byte[] activeFrame = enRealAsset;
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
                Interlocked.Increment(ref frameCounter);
                return new CaptureFrameResult
                {
                    ImageBytes = Volatile.Read(ref activeFrame),
                    ErrorKind = CaptureErrorKind.None,
                    CaptureLatencyMs = 2
                };
            });

        // Use byte-reference equality per frame swap so repeated polls of identical frame are skipped unless frame swapped
        byte[]? lastSentBytes = null;
        var changeMock = new Mock<IFrameChangeDetector>();
        changeMock.Setup(d => d.Reset()).Callback(() => Volatile.Write(ref lastSentBytes, null));
        changeMock.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>()))
            .Returns((byte[] bytes, double _) =>
            {
                var prev = Volatile.Read(ref lastSentBytes);
                if (ReferenceEquals(prev, bytes)) return false;
                Volatile.Write(ref lastSentBytes, bytes);
                return true;
            });

        var vad = new EnergyVoiceActivityDetector();
        var audioMock = new Mock<IAudioCaptureService>();
        var speechMock = new Mock<ISpeechRecognitionService>();
        speechMock.Setup(s => s.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpeechRecognitionResult
            {
                Text = "Let us double check the audio stream now.",
                DetectedLanguage = "en",
                LatencyMs = 22
            });

        await using var coordinator = new SubtitleCoordinator(
            captureMock.Object,
            ocrEngine,
            resolverMock.Object,
            settingsMock.Object,
            changeMock.Object,
            audioMock.Object,
            vad,
            speechMock.Object);

        coordinator.SetTranslationMode(TranslationMode.ScreenOcr);

        var publishedQueue = new ConcurrentQueue<(SubtitleItem Item, long CallbackMs, long CaptureMs, long OcrMs, long QueueMs, long TransMs, long E2EMs, string ModelUsed)>();
        var recognizedLog = new ConcurrentQueue<string>();
        coordinator.OcrTextRecognized += (_, ev) => recognizedLog.Enqueue($"{ev.LanguageCode}:{ev.Text}");
        TaskCompletionSource<SubtitleItem>? waiterTcs = null;
        Func<SubtitleItem, bool>? waiterPred = null;

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (!item.HasContent) return;
            var swCb = Stopwatch.StartNew();
            coordinator.LastOverlayUpdateLatencyMs = swCb.ElapsedMilliseconds;
            var rec = (
                Item: item,
                CallbackMs: swCb.ElapsedMilliseconds,
                CaptureMs: coordinator.LastCaptureLatencyMs,
                OcrMs: coordinator.LastOcrLatencyMs,
                QueueMs: coordinator.LastQueueWaitMs,
                TransMs: coordinator.LastTranslationLatencyMs,
                E2EMs: coordinator.LastEndToEndLatencyMs,
                ModelUsed: neuralEngine.LastModelUsed
            );
            publishedQueue.Enqueue(rec);

            var tcs = Volatile.Read(ref waiterTcs);
            var pred = Volatile.Read(ref waiterPred);
            if (tcs != null && (pred == null || pred(item)))
            {
                tcs.TrySetResult(item);
            }
        };

        async Task<SubtitleItem> PushFrameAndWaitAsync(byte[] pngBytes, Func<SubtitleItem, bool> match, int timeoutMs = 15000, string? label = null)
        {
            var tcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref waiterPred, match);
            Volatile.Write(ref waiterTcs, tcs);
            Volatile.Write(ref activeFrame, pngBytes);
            try
            {
                return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
            }
            catch (TimeoutException ex)
            {
                string recent = string.Join(" | ", publishedQueue.Reverse().Take(5).Select(r => $"{r.Item.SourceMode}:'{r.Item.OriginalText}'->'{r.Item.VietnameseText}'"));
                string recOcr = string.Join(" | ", recognizedLog.Reverse().Take(5));
                throw new TimeoutException(
                    $"Timeout waiting for frame [{label ?? "unknown"}] (State={coordinator.State}, Pipeline='{coordinator.PipelineStatusText}', Frames={frameCounter}, OcrAttempts={coordinator.OcrAttemptCount}, LastOcrRaw='{coordinator.LastOcrRawText}', RecOcr=[{recOcr}], Recent=[{recent}])",
                    ex);
            }
        }

        try
        {
            coordinator.Start();

            // Scenario 1: Real EN asset + crisp EN rendered frames (EnViT5)
            var subEn0 = await PushFrameAndWaitAsync(enRealAsset, s => !string.IsNullOrWhiteSpace(s.VietnameseText));
            var subEn1 = await PushFrameAndWaitAsync(renderedFrames["en_01"], s => s.OriginalText.Contains("forgot the key", StringComparison.OrdinalIgnoreCase));
            subEn1.VietnameseText.Should().NotBeNullOrWhiteSpace();
            var subEn2 = await PushFrameAndWaitAsync(renderedFrames["en_02"], s => s.OriginalText.Contains("Ctrl+Shift+S", StringComparison.OrdinalIgnoreCase));
            subEn2.VietnameseText.Should().Contain("Ctrl+Shift+S");

            // Scenario 5: Multi-line EN subtitle OCR -> joined & translated naturally via EnViT5
            var subMulti = await PushFrameAndWaitAsync(renderedFrames["multi_01"], s => s.OriginalText.Contains("qualified", StringComparison.OrdinalIgnoreCase));
            subMulti.OriginalText.Should().Contain("experience");
            subMulti.VietnameseText.Should().NotBeNullOrWhiteSpace();

            // Scenario 2: Real ZH asset + ZH rendered frame -> M2M-100 (EnableExperimentalZhViOpusModel=false)
            var subZhReal = await PushFrameAndWaitAsync(zhRealAsset, s => s.OriginalLanguageCode == "zh" && !string.IsNullOrWhiteSpace(s.VietnameseText));
            var subZh1 = await PushFrameAndWaitAsync(renderedFrames["zh_01"], s => s.OriginalLanguageCode == "zh" && !string.IsNullOrWhiteSpace(s.VietnameseText));
            neuralEngine.LastModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);

            // Scenario 3: Fast dialogue burst (rapid frame swaps, bounded queue DropOldest + latest-wins)
            var burstFinalTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref waiterPred, s => s.OriginalText.Contains("tunnel", StringComparison.OrdinalIgnoreCase));
            Volatile.Write(ref waiterTcs, burstFinalTcs);
            Volatile.Write(ref activeFrame, renderedFrames["burst_a"]);
            await Task.Delay(75);
            Volatile.Write(ref activeFrame, renderedFrames["burst_b"]);
            await Task.Delay(75);
            Volatile.Write(ref activeFrame, renderedFrames["burst_final"]);
            var burstFinal = await burstFinalTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            burstFinal.OriginalText.Should().Contain("tunnel");

            // Scenario 7: Pause / Resume / Stop / Restart
            int countBeforePause = publishedQueue.Count;
            coordinator.Pause();
            Volatile.Write(ref activeFrame, renderedFrames["en_03"]);
            await Task.Delay(220);
            coordinator.State.Should().Be(SystemState.Paused);
            publishedQueue.Count.Should().Be(countBeforePause);
            coordinator.Start(); // Resume
            var subAfterResume = await PushFrameAndWaitAsync(renderedFrames["resume_01"], s => s.OriginalText.Contains("pause and stop", StringComparison.OrdinalIgnoreCase));
            subAfterResume.VietnameseText.Should().NotBeNullOrWhiteSpace();

            coordinator.Stop();
            coordinator.State.Should().Be(SystemState.Stopped);
            coordinator.Start(); // Restart

            // Scenario 6 & 9: Region/Window update + QuickTranslate UpdateSelectedRegionAndAutoStart
            coordinator.UpdateSelectedWindow(new WindowInfo { Handle = new nint(999), Title = "MockPlayer", ProcessName = "chrome" });
            long prevSettingsVer = coordinator.SettingsVersion;
            string prevSessId = coordinator.CurrentSessionId;
            coordinator.UpdateSelectedRegionAndAutoStart(new ScreenRegion(100, 700, 1080, 160));
            coordinator.SettingsVersion.Should().BeGreaterThan(prevSettingsVer);
            coordinator.CurrentSessionId.Should().NotBe(prevSessId);
            var subQuick = await PushFrameAndWaitAsync(renderedFrames["quick_01"], s => s.OriginalText.Contains("switched region", StringComparison.OrdinalIgnoreCase));
            subQuick.SessionId.Should().Be(coordinator.CurrentSessionId);

            // Scenario 8: Hybrid OCR + Audio concurrent delivery
            coordinator.Stop();
            coordinator.SetTranslationMode(TranslationMode.Hybrid);
            coordinator.Start();
            var hybridOcrSub = await PushFrameAndWaitAsync(renderedFrames["hybrid_ocr"], s => s.OriginalText.Contains("250 ms", StringComparison.OrdinalIgnoreCase));
            hybridOcrSub.SourceMode.Should().Be(TranslationMode.ScreenOcr);

            // Feed real VAD speech energy -> silence burst through AudioDataAvailable so EnergyVoiceActivityDetector emits SpeechSegmentReady
            var speechTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref waiterPred, s => s.SourceMode == TranslationMode.AudioRealtime);
            Volatile.Write(ref waiterTcs, speechTcs);

            byte[] loudChunk = new byte[3200]; // 100ms at 16kHz 16-bit mono
            for (int i = 0; i < loudChunk.Length; i += 2)
            {
                short sample = (short)(8000 * Math.Sin(i * 0.2));
                loudChunk[i] = (byte)(sample & 0xFF);
                loudChunk[i + 1] = (byte)((sample >> 8) & 0xFF);
            }
            byte[] silentChunk = new byte[3200];
            for (int k = 0; k < 6; k++)
            {
                audioMock.Raise(a => a.AudioDataAvailable += null, audioMock.Object, new AudioDataEventArgs(loudChunk, 16000, 1));
            }
            for (int k = 0; k < 10; k++)
            {
                audioMock.Raise(a => a.AudioDataAvailable += null, audioMock.Object, new AudioDataEventArgs(silentChunk, 16000, 1));
            }
            var speechSub = await speechTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            speechSub.SourceMode.Should().Be(TranslationMode.AudioRealtime);
            speechSub.OriginalText.Should().Contain("audio stream");

            // Scenario 10: Mid-flight language/router switch & legacy rollback in Strict Offline
            coordinator.Stop();
            coordinator.SetTranslationMode(TranslationMode.ScreenOcr);
            coordinator.Start();
            var preMem = MeasureRecentPythonProcesses(startUtc);

            // Scenario 4: Continuous soak (24 unseen crisp OCR frames) to measure warm P50/P95 and RSS stability
            var soakE2Es = new List<long>();
            var soakOcr = new List<long>();
            var soakTrans = new List<long>();
            var soakQueue = new List<long>();
            for (int i = 0; i < 24; i++)
            {
                string[] markers =
                [
                    "tripod", "server rack", "Ben Thanh", "Ctrl+Alt+T",
                    "unplugged overnight", "ferry resumes", "Figma link", "Room 309",
                    "flash drive", "250 ms", "mic hums", "client email",
                    "60 fps", "fireworks begin", "platform clears", "heatsink",
                    "SHA-256", "sector zero", "salary debate", "database migration",
                    "storm finally", "PR #482", "three islands", "release tag"
                ];
                string marker = markers[i];
                var sItem = await PushFrameAndWaitAsync(renderedFrames[$"soak_{i:00}"], s => s.OriginalText.Contains(marker, StringComparison.OrdinalIgnoreCase), label: $"soak_{i:00}");
                soakE2Es.Add(coordinator.LastEndToEndLatencyMs);
                soakOcr.Add(coordinator.LastOcrLatencyMs);
                soakTrans.Add(coordinator.LastTranslationLatencyMs);
                soakQueue.Add(coordinator.LastQueueWaitMs);
            }
            var postMem = MeasureRecentPythonProcesses(startUtc);

            // Mid-flight rollback toggle while running
            long verBeforeRollback = coordinator.SettingsVersion;
            betaSettings.ApplyLegacyM2M100RollbackProfile();
            neuralEngine.EnableMultiModelRouter = false;
            coordinator.ResetTranslationCacheAndRetranslate();
            coordinator.SettingsVersion.Should().BeGreaterThan(verBeforeRollback);
            var subRollback = await PushFrameAndWaitAsync(renderedFrames["en_02"], s => s.SettingsVersion > verBeforeRollback);
            neuralEngine.LastModelUsed.Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);

            // Verify zero mock fallback occurred anywhere
            publishedQueue.Should().OnlyContain(x => !x.Item.VietnameseText.Contains("[MOCK", StringComparison.OrdinalIgnoreCase)
                                                    && !x.Item.VietnameseText.Contains("[LEXICAL", StringComparison.OrdinalIgnoreCase));
            neuralEngine.WorkerCrashOrResetCount.Should().Be(0);

            soakE2Es.Sort();
            soakOcr.Sort();
            soakTrans.Sort();
            soakQueue.Sort();
            long P50(List<long> v) => v[v.Count / 2];
            long P95(List<long> v) => v[Math.Min(v.Count - 1, (int)Math.Round(0.95 * (v.Count - 1)))];

            double rssDeltaMb = postMem.TotalRssMb - preMem.TotalRssMb;
            rssDeltaMb.Should().BeLessThan(80.0, "continuous stream should not leak memory");

            _output.WriteLine(
                $"[PHASE4_E2E] soakN={soakE2Es.Count}, " +
                $"OCR_P50={P50(soakOcr)}ms, OCR_P95={P95(soakOcr)}ms, " +
                $"Queue_P50={P50(soakQueue)}ms, Queue_P95={P95(soakQueue)}ms, " +
                $"Trans_P50={P50(soakTrans)}ms, Trans_P95={P95(soakTrans)}ms, " +
                $"E2E_P50={P50(soakE2Es)}ms, E2E_P95={P95(soakE2Es)}ms, " +
                $"Dropped={coordinator.DroppedObsoleteCount}, Stale={coordinator.RejectedStaleCount}, Crashes={neuralEngine.WorkerCrashOrResetCount}, " +
                $"PythonProcCount={postMem.Count}, PreSoakRss={preMem.TotalRssMb:F1}MB, PostSoakRss={postMem.TotalRssMb:F1}MB (delta={rssDeltaMb:F1}MB), CPU={postMem.TotalCpuSec:F2}s");
        }
        finally
        {
            coordinator.Stop();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Phase5_InstalledCt2Models_VerifySha256AndTokenizersOffline()
    {
        var settingsMock = new Mock<ISettingsService>();
        var settings = new AppSettings();
        settingsMock.Setup(s => s.Settings).Returns(settings);

        var manager = new ScreenSub.Infrastructure.LocalModelManager(settingsMock.Object);

        bool m2mOk = await manager.VerifyModelIntegrityAsync("m2m100-418m-ct2");
        bool envit5Ok = await manager.VerifyModelIntegrityAsync("envit5_ct2_int8");
        bool opusOk = await manager.VerifyModelIntegrityAsync("opus_mt_zh_vi_ct2");

        m2mOk.Should().BeTrue("m2m100_418m_ct2 model.bin SHA-256 and sentencepiece.bpe.model must match manifest");
        envit5Ok.Should().BeTrue("envit5_ct2_int8 model.bin SHA-256 and tokenizer.json/spiece.model must match manifest");
        opusOk.Should().BeTrue("opus_mt_zh_vi_ct2 model.bin SHA-256 and source.spm/target.spm must match manifest");
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;
        int n = a.Length, m = b.Length;
        int[] prev = new int[m + 1];
        int[] curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }

    private static string NormalizeForCer(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        return string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        double idx = (p / 100.0) * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    [Fact]
    public async Task Phase3_RealOcrWorker_MultiConditionAccuracyLatency_AndDuplicateSuppression()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ScreenSub_Phase3_Ocr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string specPath = Path.Combine(tempDir, "phase3_spec.json");
            string framesDir = Path.Combine(tempDir, "frames");
            Directory.CreateDirectory(framesDir);

            var cases = new[]
            {
                new { id = "en_conv_1", text = "We're just having a drink.", lang = "en", w = 1280, h = 180, font = 34, bg = "dark", style = "normal" },
                new { id = "en_conv_2", text = "Don't get me wrong, I never said that.", lang = "en", w = 1280, h = 180, font = 34, bg = "dark", style = "normal" },
                new { id = "en_neg_3", text = "I couldn't care less about what they think.", lang = "en", w = 1280, h = 180, font = 34, bg = "dark", style = "normal" },
                new { id = "en_multi_4", text = "Wait a second!\nDid you really leave the door open?", lang = "en", w = 1280, h = 210, font = 32, bg = "dark", style = "normal" },
                new { id = "en_small_18px", text = "Check the timestamp on page 14 before 9:30 PM.", lang = "en", w = 1280, h = 160, font = 18, bg = "dark", style = "normal" },
                new { id = "zhs_1", text = "我们只是喝一杯，别想太多。", lang = "zhs", w = 1280, h = 180, font = 36, bg = "dark", style = "normal" },
                new { id = "zhs_mix_2", text = "会议将在10分钟后开始，请打开第3页。", lang = "zhs", w = 1280, h = 180, font = 36, bg = "dark", style = "normal" },
                new { id = "zhs_multi_3", text = "别误会我的意思\n我只是不想再浪费时间了", lang = "zhs", w = 1280, h = 210, font = 34, bg = "dark", style = "normal" },
                new { id = "zhs_small_18px", text = "请确认网络连接稳定后再尝试。", lang = "zhs", w = 1280, h = 160, font = 18, bg = "dark", style = "normal" },
                new { id = "zht_1", text = "請先關閉系統設定再重新啟動程式。", lang = "zht", w = 1280, h = 180, font = 36, bg = "dark", style = "normal" },
                new { id = "zht_2", text = "這間餐廳雖然價格偏高但非常值得。", lang = "zht", w = 1280, h = 180, font = 36, bg = "dark", style = "normal" },
                new { id = "zht_multi_3", text = "時間已經不早了\n我們還是明天再談吧", lang = "zht", w = 1280, h = 210, font = 34, bg = "dark", style = "normal" },
                new { id = "bg_light_en", text = "Turn left at the next traffic light.", lang = "en", w = 1280, h = 180, font = 34, bg = "light", style = "normal" },
                new { id = "bg_light_zh", text = "请在下一个路口向左转。", lang = "zhs", w = 1280, h = 180, font = 36, bg = "light", style = "normal" },
                new { id = "bg_low_contrast", text = "Nobody noticed him slip out quietly.", lang = "en", w = 1280, h = 180, font = 34, bg = "lowcontrast", style = "normal" },
                new { id = "bg_shadow_outline_en", text = "You've got to be kidding me!", lang = "en", w = 1280, h = 180, font = 36, bg = "noisy", style = "outline" },
                new { id = "bg_shadow_outline_zh", text = "这怎么可能呢？我刚刚才见过他。", lang = "zhs", w = 1280, h = 180, font = 36, bg = "noisy", style = "outline" },
                new { id = "dpi_100_en", text = "Let's wrap this up before noon.", lang = "en", w = 1280, h = 180, font = 32, bg = "dark", style = "normal" },
                new { id = "dpi_125_en", text = "Everything looks good so far.", lang = "en", w = 1600, h = 225, font = 40, bg = "dark", style = "normal" },
                new { id = "dpi_150_zh", text = "今天天气真不错，我们出去走走吧。", lang = "zhs", w = 1920, h = 270, font = 48, bg = "dark", style = "normal" },
                new { id = "dpi_200_zh", text = "這部電影拍得非常精彩。", lang = "zht", w = 2560, h = 360, font = 64, bg = "dark", style = "normal" },
                new { id = "wide_strip_1920", text = "Hold on, let me double-check the schedule.", lang = "en", w = 1920, h = 120, font = 36, bg = "dark", style = "normal" },
            };

            await File.WriteAllTextAsync(specPath, System.Text.Json.JsonSerializer.Serialize(cases));
            string renderScript = Path.Combine(tempDir, "render_phase3.py");
            await File.WriteAllTextAsync(renderScript, """
                import json, os, sys
                from PIL import Image, ImageDraw, ImageFont

                spec_path, out_dir = sys.argv[1], sys.argv[2]
                with open(spec_path, "r", encoding="utf-8") as f:
                    cases = json.load(f)

                def load_font(size, is_cjk):
                    candidates = (
                        ["msyh.ttc", "msyhbd.ttc", "simhei.ttf", "arial.ttf"]
                        if is_cjk else
                        ["segoeui.ttf", "arial.ttf", "msyh.ttc"]
                    )
                    for name in candidates:
                        for base in [r"C:\Windows\Fonts", ""]:
                            p = os.path.join(base, name) if base else name
                            if os.path.exists(p):
                                try:
                                    return ImageFont.truetype(p, size)
                                except Exception:
                                    pass
                    return ImageFont.load_default()

                def render_one(item, out_name, bg_shift=False):
                    w, h = item["w"], item["h"]
                    bg_mode = item["bg"]
                    if bg_mode == "light":
                        img = Image.new("RGB", (w, h), (242, 244, 248))
                        fg = (18, 24, 38)
                        stroke_col = None
                    elif bg_mode == "lowcontrast":
                        img = Image.new("RGB", (w, h), (78, 86, 98))
                        fg = (224, 228, 234)
                        stroke_col = (40, 44, 52)
                    elif bg_mode == "noisy":
                        img = Image.new("RGB", (w, h), (35, 50, 70))
                        d0 = ImageDraw.Draw(img)
                        for x in range(0, w, 40):
                            c = 25 + ((x * 7 + (97 if bg_shift else 13)) % 75)
                            d0.rectangle([x, 0, x + 38, h], fill=(c, c + 12, c + 24))
                        d0.rectangle([int(w * 0.08), int(h * 0.16), int(w * 0.92), int(h * 0.86)], fill=(20, 26, 36))
                        fg = (255, 255, 255)
                        stroke_col = (0, 0, 0)
                    else:
                        img = Image.new("RGB", (w, h), (16, 20, 28))
                        if bg_shift:
                            d0 = ImageDraw.Draw(img)
                            d0.rectangle([0, 0, w, 28], fill=(95, 110, 130))
                            d0.rectangle([0, h - 24, w, h], fill=(70, 85, 105))
                        fg = (248, 250, 252)
                        stroke_col = None

                    draw = ImageDraw.Draw(img)
                    font = load_font(item["font"], item["lang"] != "en")
                    text = item["text"]
                    bbox = draw.multiline_textbbox((0, 0), text, font=font, spacing=8, align="center")
                    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
                    x = max(12, (w - tw) // 2)
                    y = max(10, (h - th) // 2)
                    if stroke_col and item["style"] == "outline":
                        draw.multiline_text((x + 2, y + 2), text, font=font, fill=(10, 10, 10), spacing=8, align="center")
                        draw.multiline_text((x, y), text, font=font, fill=fg, stroke_width=2, stroke_fill=stroke_col, spacing=8, align="center")
                    else:
                        draw.multiline_text((x, y), text, font=font, fill=fg, spacing=8, align="center")
                    img.save(os.path.join(out_dir, out_name), "PNG")

                for item in cases:
                    render_one(item, f"{item['id']}.png", bg_shift=False)

                render_one(cases[2], "en_neg_3_bgshift.png", bg_shift=True)
                """);

            var renderPsi = new ProcessStartInfo("python", $"\"{renderScript}\" \"{specPath}\" \"{framesDir}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var renderProc = Process.Start(renderPsi)!)
            {
                string err = await renderProc.StandardError.ReadToEndAsync();
                await renderProc.WaitForExitAsync();
                renderProc.ExitCode.Should().Be(0, $"PIL subtitle renderer must succeed: {err}");
            }

            string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
            await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
            await ocrEngine.InitializeAsync();
            ocrEngine.Status.Should().Be(ServiceStatus.Running);

            // Warmup once
            byte[] warmBytes = await File.ReadAllBytesAsync(Path.Combine(framesDir, $"{cases[0].id}.png"));
            _ = await ocrEngine.RecognizeAsync(warmBytes);

            var latencies = new List<double>();
            var decodes = new List<double>();
            var dets = new List<double>();
            var recs = new List<double>();
            var cers = new List<double>();
            int exactOrNear = 0;

            foreach (var item in cases)
            {
                byte[] imgBytes = await File.ReadAllBytesAsync(Path.Combine(framesDir, $"{item.id}.png"));
                var sw = Stopwatch.StartNew();
                var res = await ocrEngine.RecognizeAsync(imgBytes);
                sw.Stop();

                double ms = sw.Elapsed.TotalMilliseconds;
                latencies.Add(ms);
                decodes.Add(ocrEngine.LastDecodeMs);
                dets.Add(ocrEngine.LastDetMs);
                recs.Add(ocrEngine.LastRecMs);

                string expectedNorm = NormalizeForCer(item.text);
                string actualNorm = NormalizeForCer(res.FullText);
                double cer = expectedNorm.Length > 0
                    ? (double)LevenshteinDistance(expectedNorm, actualNorm) / expectedNorm.Length * 100.0
                    : 0.0;
                cers.Add(cer);
                if (cer <= 5.0) exactOrNear++;

                _output.WriteLine($"[PHASE3_CASE] {item.id}: {ms:F1}ms (dec={ocrEngine.LastDecodeMs:F1}, det={ocrEngine.LastDetMs:F1}, rec={ocrEngine.LastRecMs:F1}) CER={cer:F2}% | got='{actualNorm}'");
            }

            var sortedLat = latencies.OrderBy(x => x).ToList();
            double p50 = Percentile(sortedLat, 50);
            double p95 = Percentile(sortedLat, 95);
            double meanLat = latencies.Average();
            double meanCer = cers.Average();

            // Coordinator static idle-frame suppression + bg-shift duplicate subtitle suppression check
            var settingsSvc = new ScreenSub.Infrastructure.SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
            settingsSvc.Settings.CaptureFps = 10;
            settingsSvc.Settings.TranslationMode = TranslationMode.ScreenOcr;
            settingsSvc.Settings.TranslationEngineMode = TranslationEngineMode.Offline;
            settingsSvc.Settings.SourceLanguage = SourceLanguage.English;
            settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];
            settingsSvc.Settings.LastRegion = new ScreenRegion(0, 0, 1280, 180);

            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await transEngine.InitializeAsync();
            // Warm EnViT5 once so coordinator timings reflect steady-state translation
            await transEngine.TranslateTextAsync("Warmup check.", "en", "vi");

            var cache = new TranslationCache();
            using var localProvider = new LocalTranslationProvider(transEngine, cache, settingsSvc);
            var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache);
            var mock = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azure, mock);
            var realDetector = new ScreenSub.Capture.FrameChangeDetector();

            byte[] frameStatic = warmBytes;
            byte[] frameChanged = await File.ReadAllBytesAsync(Path.Combine(framesDir, "en_neg_3.png"));
            byte[] frameBgShiftSameText = await File.ReadAllBytesAsync(Path.Combine(framesDir, "en_neg_3_bgshift.png"));
            int emitPhase = 0; // 0 = identical warmBytes, 1 = en_neg_3, 2 = en_neg_3_bgshift
            var publishedSubtitles = new ConcurrentQueue<string>();
            var firstPubTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPubTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var captureMock = new Mock<IScreenCaptureService>();
            captureMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                    It.IsAny<ScreenRegion>(),
                    It.IsAny<WindowInfo?>(),
                    It.IsAny<RegionMode>(),
                    It.IsAny<CaptureSourceType>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new CaptureFrameResult
                {
                    ImageBytes = Volatile.Read(ref emitPhase) switch
                    {
                        0 => frameStatic,
                        1 => frameChanged,
                        _ => frameBgShiftSameText
                    },
                    ErrorKind = CaptureErrorKind.None,
                    CaptureLatencyMs = 1
                });

            await using var coordinator = new SubtitleCoordinator(captureMock.Object, ocrEngine, resolver, settingsSvc, realDetector);
            coordinator.SubtitleUpdated += (_, sub) =>
            {
                if (sub.HasContent)
                {
                    publishedSubtitles.Enqueue(sub.OriginalText);
                    if (sub.OriginalText.Contains("having a drink", StringComparison.OrdinalIgnoreCase))
                        firstPubTcs.TrySetResult(true);
                    if (sub.OriginalText.Contains("couldn't care less", StringComparison.OrdinalIgnoreCase))
                        secondPubTcs.TrySetResult(true);
                }
            };

            coordinator.Start();
            await firstPubTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1200); // additional unchanged static frames must skip OCR
            long skippedIdle = coordinator.FrameSkippedUnchangedCount;
            long attemptsAfterIdle = coordinator.OcrAttemptCount;

            Volatile.Write(ref emitPhase, 1);
            await secondPubTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref emitPhase, 2); // pixel border changed, subtitle text identical -> DuplicateSuppressed
            await Task.Delay(700);
            coordinator.Stop();

            _output.WriteLine($"[PHASE3_OCR] Cases={cases.Length}, P50={p50:F1}ms, P95={p95:F1}ms, Mean={meanLat:F1}ms, MeanDecode={decodes.Average():F1}ms, MeanDet={dets.Average():F1}ms, MeanRec={recs.Average():F1}ms, MeanCER={meanCer:F2}%, ExactOrNear={exactOrNear}/{cases.Length}, IdleSkipped={skippedIdle}, IdleOcrAttempts={attemptsAfterIdle}, DupSuppressed={coordinator.DuplicateSuppressedCount}");

            meanCer.Should().BeLessThan(5.0);
            exactOrNear.Should().BeGreaterThanOrEqualTo(18);
            p50.Should().BeLessThan(200.0);
            p95.Should().BeLessThan(350.0);
            skippedIdle.Should().BeGreaterThanOrEqualTo(5, "unchanged idle frames must be skipped without looping OCR every 350ms");
            attemptsAfterIdle.Should().BeLessThanOrEqualTo(2);
            coordinator.DuplicateSuppressedCount.Should().BeGreaterThanOrEqualTo(1, "identical OCR text after background shift should not re-trigger translation");
            publishedSubtitles.Count.Should().Be(2);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Phase4_ExtendedSoakAndStress_WorkerLifecycleCancellationAndMemory()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ScreenSub_Phase4_Soak_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var scriptFrames = new Dictionary<string, string>
            {
                ["en_0"] = "We're just having a drink.",
                ["en_1"] = "Don't take it personally, she's always this direct.",
                ["en_2"] = "Honestly, I didn't see that coming at all.",
                ["en_3"] = "Could you hold on for just one minute?",
                ["en_4"] = "He couldn't care less about the deadline.",
                ["zh_0"] = "我们只是喝一杯，别想太多。",
                ["zh_1"] = "事情没有你想象的那么复杂。",
                ["zh_2"] = "请问附近有地铁站吗？",
                ["zh_3"] = "我刚刚才从车站走过来。",
            };
            var frameBytes = await RenderSubtitleFramesAsync(tempDir, scriptFrames);

            var settingsSvc = new ScreenSub.Infrastructure.SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
            settingsSvc.Settings.CaptureFps = 12;
            settingsSvc.Settings.TranslationMode = TranslationMode.Hybrid;
            settingsSvc.Settings.TranslationEngineMode = TranslationEngineMode.Offline;
            settingsSvc.Settings.SourceLanguage = SourceLanguage.English;
            settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];
            settingsSvc.Settings.LastRegion = new ScreenRegion(0, 0, 1280, 180);

            string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");

            await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await Task.WhenAll(ocrEngine.InitializeAsync(), transEngine.InitializeAsync());

            var cache = new TranslationCache();
            using var localProvider = new LocalTranslationProvider(transEngine, cache, settingsSvc);
            var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache);
            var mock = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azure, mock);
            var changeDetector = new ScreenSub.Capture.FrameChangeDetector();

            var audioMock = new Mock<IAudioCaptureService>();
            var vadMock = new Mock<IVoiceActivityDetector>();
            var speechMock = new Mock<ISpeechRecognitionService>();
            speechMock.Setup(s => s.Status).Returns(ServiceStatus.Running);

            string activeKey = "en_0";
            var captureMock = new Mock<IScreenCaptureService>();
            captureMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                    It.IsAny<ScreenRegion>(),
                    It.IsAny<WindowInfo?>(),
                    It.IsAny<RegionMode>(),
                    It.IsAny<CaptureSourceType>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new CaptureFrameResult
                {
                    ImageBytes = frameBytes[Volatile.Read(ref activeKey)],
                    ErrorKind = CaptureErrorKind.None,
                    CaptureLatencyMs = 1
                });

            await using var coordinator = new SubtitleCoordinator(
                captureMock.Object, ocrEngine, resolver, settingsSvc, changeDetector,
                audioMock.Object, vadMock.Object, speechMock.Object);

            long publishedCount = 0;
            int errorSubtitles = 0;
            coordinator.SubtitleUpdated += (_, sub) =>
            {
                if (sub.HasContent)
                {
                    Interlocked.Increment(ref publishedCount);
                    if (sub.VietnameseText.Contains("Mock", StringComparison.OrdinalIgnoreCase) ||
                        sub.VietnameseText.Contains("[Error", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref errorSubtitles);
                    }
                }
            };

            static (double hostWsMb, double hostPrivMb, double pyWsMb, double pyPrivMb, int pyCount) SampleMemory()
            {
                var host = Process.GetCurrentProcess();
                host.Refresh();
                var pyProcs = Process.GetProcessesByName("python");
                double pyWs = 0, pyPriv = 0;
                foreach (var p in pyProcs)
                {
                    try { p.Refresh(); pyWs += p.WorkingSet64; pyPriv += p.PrivateMemorySize64; } catch { }
                    finally { p.Dispose(); }
                }
                return (
                    host.WorkingSet64 / (1024.0 * 1024.0),
                    host.PrivateMemorySize64 / (1024.0 * 1024.0),
                    pyWs / (1024.0 * 1024.0),
                    pyPriv / (1024.0 * 1024.0),
                    pyProcs.Length);
            }

            // Warmup across EN + ZH models first so LRU model weights are accounted for before soak delta
            coordinator.Start();
            Volatile.Write(ref activeKey, "en_0");
            await Task.Delay(700);
            settingsSvc.Settings.SourceLanguage = SourceLanguage.Chinese;
            Volatile.Write(ref activeKey, "zh_0");
            await Task.Delay(900);
            settingsSvc.Settings.SourceLanguage = SourceLanguage.English;

            var memBaseline = SampleMemory();
            var cpuBaseline = Process.GetCurrentProcess().TotalProcessorTime;
            var soakSw = Stopwatch.StartNew();

            var enKeys = new[] { "en_0", "en_1", "en_2", "en_3", "en_4" };
            var zhKeys = new[] { "zh_0", "zh_1", "zh_2", "zh_3" };
            bool isZh = false;
            int cycle = 0;
            int stopRestartCycles = 0;
            int burstCycles = 0;
            var memMid = memBaseline;
            bool capturedMid = false;

            while (soakSw.Elapsed < TimeSpan.FromSeconds(75))
            {
                cycle++;
                if (cycle % 24 == 0)
                {
                    stopRestartCycles++;
                    coordinator.Stop();
                    isZh = !isZh;
                    settingsSvc.Settings.SourceLanguage = isZh ? SourceLanguage.Chinese : SourceLanguage.English;
                    Volatile.Write(ref activeKey, isZh ? zhKeys[cycle % zhKeys.Length] : enKeys[cycle % enKeys.Length]);
                    coordinator.Start();
                    await Task.Delay(500);
                }
                else if (cycle % 10 == 0)
                {
                    burstCycles++;
                    var pool = isZh ? zhKeys : enKeys;
                    Volatile.Write(ref activeKey, pool[(cycle + 0) % pool.Length]);
                    await Task.Delay(70);
                    Volatile.Write(ref activeKey, pool[(cycle + 1) % pool.Length]);
                    await Task.Delay(70);
                    Volatile.Write(ref activeKey, pool[(cycle + 2) % pool.Length]);
                    await Task.Delay(520);
                }
                else
                {
                    var pool = isZh ? zhKeys : enKeys;
                    Volatile.Write(ref activeKey, pool[cycle % pool.Length]);
                    await Task.Delay(cycle % 5 == 0 ? 900 : 480);
                }

                if (cycle % 8 == 0)
                {
                    vadMock.Raise(v => v.SpeechSegmentReady += null, vadMock.Object, new SpeechSegment
                    {
                        Id = cycle,
                        PcmData = Array.Empty<byte>(),
                        SampleRate = 16000
                    });
                }

                if (!capturedMid && soakSw.Elapsed >= TimeSpan.FromSeconds(37))
                {
                    memMid = SampleMemory();
                    capturedMid = true;
                }
            }

            coordinator.Stop();
            soakSw.Stop();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            var memEnd = SampleMemory();
            double hostCpuSec = (Process.GetCurrentProcess().TotalProcessorTime - cpuBaseline).TotalSeconds;
            double hostPrivDeltaMb = memEnd.hostPrivMb - memBaseline.hostPrivMb;
            double pyWsDeltaMb = memEnd.pyWsMb - memBaseline.pyWsMb;
            double pyPrivMidToEndDeltaMb = memEnd.pyPrivMb - memMid.pyPrivMb;

            _output.WriteLine(
                $"[PHASE4_SOAK] Duration={soakSw.Elapsed.TotalSeconds:F1}s, Cycles={cycle}, StopRestarts={stopRestartCycles}, Bursts={burstCycles}, " +
                $"Published={publishedCount}, OcrAttempts={coordinator.OcrAttemptCount}, OcrSuccess={coordinator.OcrSuccessCount}, " +
                $"SkippedUnchanged={coordinator.FrameSkippedUnchangedCount}, DupSuppressed={coordinator.DuplicateSuppressedCount}, " +
                $"DroppedObsolete={coordinator.DroppedObsoleteCount}, RejectedStale={coordinator.RejectedStaleCount}, " +
                $"QueueLen={coordinator.OcrQueueLength}, ErrorSubs={errorSubtitles}, WorkerCrashOrReset={transEngine.WorkerCrashOrResetCount}, HostCpu={hostCpuSec:F2}s, " +
                $"MemHostPriv(MB): Base={memBaseline.hostPrivMb:F1} Mid={memMid.hostPrivMb:F1} End={memEnd.hostPrivMb:F1} (Delta={hostPrivDeltaMb:+0.0;-0.0}), " +
                $"MemPyRss(MB): Base={memBaseline.pyWsMb:F1} Mid={memMid.pyWsMb:F1} End={memEnd.pyWsMb:F1} (Delta={pyWsDeltaMb:+0.0;-0.0}), " +
                $"MemPyPriv(MB): Base={memBaseline.pyPrivMb:F1} Mid={memMid.pyPrivMb:F1} End={memEnd.pyPrivMb:F1} (MidEndDelta={pyPrivMidToEndDeltaMb:+0.0;-0.0}, PyProcs={memEnd.pyCount})");

            ocrEngine.Status.Should().Be(ServiceStatus.Running);
            transEngine.State.Should().Be(ModelRuntimeState.Ready);
            transEngine.WorkerCrashOrResetCount.Should().Be(0);
            errorSubtitles.Should().Be(0, "no mock or translation errors should surface during soak");
            publishedCount.Should().BeGreaterThanOrEqualTo(30);
            coordinator.OcrQueueLength.Should().BeLessThanOrEqualTo(2);
            hostPrivDeltaMb.Should().BeLessThan(150.0, ".NET host private memory must stay bounded across soak cycles");
            memEnd.pyWsMb.Should().BeLessThan(1800.0, "Python OCR + translation LRU workers RSS must stay within memory budget");
            pyWsDeltaMb.Should().BeLessThan(200.0, "Python workers RSS must not grow unboundedly across soak cycles");
            pyPrivMidToEndDeltaMb.Should().BeLessThan(200.0, "Python workers virtual commit must plateau by soak midpoint");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
