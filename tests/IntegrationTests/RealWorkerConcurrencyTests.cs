using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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

namespace IntegrationTests;

public class RealWorkerConcurrencyTests
{
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

    private static AppSettings CreateLocalSettings() => new()
    {
        CaptureFps = 10,
        TranslationMode = TranslationMode.ScreenOcr,
        SourceLanguage = SourceLanguage.English,
        TargetLanguages = [TargetLanguage.Vietnamese],
        DisplayMode = DisplayMode.Triple,
        EnableContextAwareTranslation = false,
        LastRegion = new ScreenRegion(100, 100, 600, 120)
    };

    [Fact]
    public async Task RealWorker_StopAndRestart_ShouldDiscardStaleSessionTranslationAndStayHealthy()
    {
        LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk().Should().BeTrue("m2m100_418m_ct2 must be present for real-worker verification");

        string workerPath = ResolveWorkerScriptPath();
        var settings = CreateLocalSettings();
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(settings);
        settingsMock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);

        using var engine = new LocalNeuralTranslationEngine("python", workerPath);
        await engine.InitializeAsync();
        engine.State.Should().Be(ModelRuntimeState.Ready);

        var cache = new TranslationCache();
        using var localProvider = new LocalTranslationProvider(engine, cache, settingsMock.Object);

        // Warm up model inside worker once so inference time is deterministic (~280ms)
        var warm = await localProvider.TranslateAsync("Warmup.", SourceLanguage.English, [TargetLanguage.Vietnamese]);
        warm.Translations[TargetLanguage.Vietnamese].Should().NotBeNullOrWhiteSpace();

        string activeOcrText = "Please tell David I will arrive before midnight.";
        int frameId = 0;
        var captureMock = new Mock<IScreenCaptureService>();
        captureMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                It.IsAny<ScreenRegion>(),
                It.IsAny<WindowInfo?>(),
                It.IsAny<RegionMode>(),
                It.IsAny<CaptureSourceType>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                int id = Interlocked.Increment(ref frameId);
                return new CaptureFrameResult
                {
                    ImageBytes = [(byte)(id & 0xFF), 7, 8, 9],
                    ErrorKind = CaptureErrorKind.None,
                    CaptureLatencyMs = 1
                };
            });

        var changeDetectorMock = new Mock<IFrameChangeDetector>();
        changeDetectorMock.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>())).Returns(true);

        var firstRecognized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Connected);
        ocrMock.Setup(o => o.StatusMessage).Returns("Ready");
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var txt = Volatile.Read(ref activeOcrText);
                if (txt.StartsWith("Please tell David", StringComparison.Ordinal))
                {
                    firstRecognized.TrySetResult(true);
                }
                return new OcrResult { FullText = txt, Confidence = 0.99, LatencyMs = 1 };
            });

        var resolverMock = new Mock<ITranslationProviderResolver>();
        resolverMock.Setup(r => r.ResolveActiveProvider()).Returns(localProvider);
        resolverMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(localProvider);
        resolverMock.Setup(r => r.ActiveProviderBadgeText).Returns("LOCAL");

        await using var coordinator = new SubtitleCoordinator(
            captureMock.Object,
            ocrMock.Object,
            resolverMock.Object,
            settingsMock.Object,
            changeDetectorMock.Object);

        var session1Items = new ConcurrentQueue<SubtitleItem>();
        var session2Items = new ConcurrentQueue<SubtitleItem>();
        int currentPhase = 1;
        var session2Published = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (Volatile.Read(ref currentPhase) == 1)
            {
                session1Items.Enqueue(item);
            }
            else
            {
                session2Items.Enqueue(item);
                if (item.OriginalText == "Let's wrap this up before five o'clock.")
                {
                    session2Published.TrySetResult(item);
                }
            }
        };

        coordinator.Start();
        await firstRecognized.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Stop while real worker is translating session 1 text
        coordinator.Stop();
        Volatile.Write(ref currentPhase, 2);
        Volatile.Write(ref activeOcrText, "Let's wrap this up before five o'clock.");

        coordinator.Start();
        try
        {
            var s2Winner = await session2Published.Task.WaitAsync(TimeSpan.FromSeconds(15));
            coordinator.Stop();

            s2Winner.VietnameseText.Should().NotBeNullOrWhiteSpace();
            session2Items.Should().OnlyContain(i => i.OriginalText != "Please tell David I will arrive before midnight.");
            engine.WorkerCrashOrResetCount.Should().Be(0);
        }
        catch (TimeoutException tex)
        {
            throw new TimeoutException(
                $"{tex.Message} [DIAG State={coordinator.State}, Status='{coordinator.PipelineStatusText}', OcrAtt={coordinator.OcrAttemptCount}, OcrOk={coordinator.OcrSuccessCount}, DupSup={coordinator.DuplicateSuppressedCount}, Dropped={coordinator.DroppedObsoleteCount}, Rejected={coordinator.RejectedStaleCount}, EngState={engine.State}, Crashes={engine.WorkerCrashOrResetCount}, S2Count={session2Items.Count}]",
                tex);
        }
    }

    [Fact]
    public async Task RealWorker_ConcurrentBurstAndPartialCancellation_ShouldPreserveIpcOrderWithoutDesync()
    {
        LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk().Should().BeTrue();

        string workerPath = ResolveWorkerScriptPath();
        using var engine = new LocalNeuralTranslationEngine("python", workerPath);
        await engine.InitializeAsync();
        engine.State.Should().Be(ModelRuntimeState.Ready);

        // Warm up once
        await engine.TranslateTextAsync("Hello.", "en", "vi");

        var inputs = new[]
        {
            ("Thank you very much.", "cảm ơn"),
            ("Good morning everyone.", "chào"),
            ("It is raining heavily outside.", "mưa"),
            ("I forgot my umbrella at home.", "ô"),
            ("Please wait for five minutes.", "chờ")
        };

        using var shortCancelCts = new CancellationTokenSource(50);

        // Fire item 0 uncancelled, item 1 cancelled early while waiting for _lock or mid-IPC, items 2..4 uncancelled
        var t0 = engine.TranslateTextAsync(inputs[0].Item1, "en", "vi");
        var t1 = engine.TranslateTextAsync(inputs[1].Item1, "en", "vi", shortCancelCts.Token);
        var t2 = engine.TranslateTextAsync(inputs[2].Item1, "en", "vi");
        var t3 = engine.TranslateTextAsync(inputs[3].Item1, "en", "vi");
        var t4 = engine.TranslateTextAsync(inputs[4].Item1, "en", "vi");

        var r0 = await t0;
        Func<Task> act1 = async () => await t1;
        await act1.Should().ThrowAsync<OperationCanceledException>();
        var r2 = await t2;
        var r3 = await t3;
        var r4 = await t4;

        r0.ToLowerInvariant().Should().Contain("cảm ơn");
        r2.ToLowerInvariant().Should().Contain("mưa");
        r3.Should().NotBeNullOrWhiteSpace();
        r4.Should().NotBeNullOrWhiteSpace();
        r2.Should().NotBe(r0);
        r3.Should().NotBe(r2);

        // Worker must remain completely synchronized after caller cancellation
        var finalCheck = await engine.TranslateTextAsync("See you tomorrow.", "en", "vi");
        finalCheck.Should().NotBeNullOrWhiteSpace();
        engine.WorkerCrashOrResetCount.Should().Be(0);
    }

    [Fact]
    public async Task RealWorker_DecodingParameterChangeAndCacheReset_ShouldInvalidateCacheAndRetranslate()
    {
        LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk().Should().BeTrue();

        string workerPath = ResolveWorkerScriptPath();
        var settings = CreateLocalSettings();
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(settings);

        using var engine = new LocalNeuralTranslationEngine("python", workerPath);
        await engine.InitializeAsync();
        engine.State.Should().Be(ModelRuntimeState.Ready);

        var cache = new TranslationCache();
        using var provider = new LocalTranslationProvider(engine, cache, settingsMock.Object);

        const string sentence = "Don't worry, everything will be fine.";

        var r1 = await provider.TranslateAsync(sentence, SourceLanguage.English, [TargetLanguage.Vietnamese]);
        r1.Translations[TargetLanguage.Vietnamese].Should().NotBeNullOrWhiteSpace();
        provider.StatusMessage.Should().StartWith("Offline AI (M2M-100");

        // Second call with identical decoding settings -> must hit cache
        var r2 = await provider.TranslateAsync(sentence, SourceLanguage.English, [TargetLanguage.Vietnamese]);
        provider.StatusMessage.Should().Be("Offline AI (Cache Hit)");
        r2.Translations[TargetLanguage.Vietnamese].Should().Be(r1.Translations[TargetLanguage.Vietnamese]);

        // Changing decoding config changes engine.CacheIdentity -> must bypass old cache entry and re-run real worker
        engine.BeamSize = 4;
        var r3 = await provider.TranslateAsync(sentence, SourceLanguage.English, [TargetLanguage.Vietnamese]);
        provider.StatusMessage.Should().StartWith("Offline AI (M2M-100");
        r3.Translations[TargetLanguage.Vietnamese].Should().NotBeNullOrWhiteSpace();
        engine.WorkerCrashOrResetCount.Should().Be(0);
    }

    [Fact]
    public async Task RealWorker_RepeatedStopRestartStress_25Cycles_ShouldStayHealthyAndLeakFree()
    {
        LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk().Should().BeTrue();

        string workerPath = ResolveWorkerScriptPath();
        var settings = CreateLocalSettings();
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(settings);
        settingsMock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);

        using var engine = new LocalNeuralTranslationEngine("python", workerPath);
        await engine.InitializeAsync();
        await engine.TranslateTextAsync("Warmup.", "en", "vi");

        var cache = new TranslationCache();
        using var localProvider = new LocalTranslationProvider(engine, cache, settingsMock.Object);

        int frameId = 0;
        string activeText = "Stress cycle 0.";
        var captureMock = new Mock<IScreenCaptureService>();
        captureMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                It.IsAny<ScreenRegion>(), It.IsAny<WindowInfo?>(), It.IsAny<RegionMode>(), It.IsAny<CaptureSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CaptureFrameResult
            {
                ImageBytes = [(byte)(Interlocked.Increment(ref frameId) & 0xFF), 1, 2],
                ErrorKind = CaptureErrorKind.None,
                CaptureLatencyMs = 1
            });

        var chMock = new Mock<IFrameChangeDetector>();
        chMock.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>())).Returns(true);

        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Running);
        ocrMock.Setup(o => o.StatusMessage).Returns("Running");
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new OcrResult { FullText = Volatile.Read(ref activeText), Confidence = 0.99, LatencyMs = 1 });

        var resMock = new Mock<ITranslationProviderResolver>();
        resMock.Setup(r => r.ResolveActiveProvider()).Returns(localProvider);
        resMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(localProvider);
        resMock.Setup(r => r.ActiveProviderBadgeText).Returns("LOCAL");

        await using var coordinator = new SubtitleCoordinator(captureMock.Object, ocrMock.Object, resMock.Object, settingsMock.Object, chMock.Object);
        var finalPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? unexpectedExceptionStatus = null;

        coordinator.StatusUpdated += (_, st) =>
        {
            if (st.Contains("ObjectDisposed", StringComparison.OrdinalIgnoreCase))
            {
                unexpectedExceptionStatus = st;
            }
        };
        coordinator.SubtitleUpdated += (_, sub) =>
        {
            if (sub.HasContent && sub.OriginalText == "Final cycle settled safely.")
            {
                finalPublished.TrySetResult(sub);
            }
        };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        long handlesStart = proc.HandleCount;

        for (int i = 1; i <= 25; i++)
        {
            Volatile.Write(ref activeText, $"Stress cycle sentence number {i}.");
            coordinator.Start();
            await Task.Delay(35);
            coordinator.Stop();
        }

        Volatile.Write(ref activeText, "Final cycle settled safely.");
        coordinator.Start();
        var winner = await finalPublished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        coordinator.Stop();

        proc.Refresh();
        long handleDelta = proc.HandleCount - handlesStart;

        unexpectedExceptionStatus.Should().BeNull("CTS retirement must never raise ObjectDisposedException");
        winner.VietnameseText.Should().NotBeNullOrWhiteSpace();
        engine.WorkerCrashOrResetCount.Should().Be(0);
        handleDelta.Should().BeLessThan(80, "25 Stop/Restart cycles must not leak OS or wait handles");
    }
}
