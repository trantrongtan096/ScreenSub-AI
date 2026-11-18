using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using Moq;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using Xunit;

namespace IntegrationTests;

public class FrameChangeAndRetrySafetyTests
{
    private static AppSettings CreateSettings() => new()
    {
        CaptureFps = 10,
        TranslationMode = TranslationMode.ScreenOcr,
        SourceLanguage = SourceLanguage.English,
        TargetLanguages = [TargetLanguage.Vietnamese],
        DisplayMode = DisplayMode.Triple,
        FrameDiffThreshold = 0.03,
        EnableContextAwareTranslation = false,
        LastRegion = new ScreenRegion(0, 0, 1280, 180)
    };

    private static (
        SubtitleCoordinator Coordinator,
        Mock<IOcrEngine> OcrMock,
        Mock<ITranslationProvider> ProviderMock) CreateHarness(
            Func<CaptureFrameResult> captureSupplier,
            IFrameChangeDetector changeDetector,
            Func<byte[], CancellationToken, Task<OcrResult>> ocrHandler,
            Func<string, SourceLanguage, IReadOnlyList<TargetLanguage>, CancellationToken, Task<TranslationResult>>? transHandler = null,
            ServiceStatus ocrStatus = ServiceStatus.Running)
    {
        var settings = CreateSettings();
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(settings);
        settingsMock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);

        var captureMock = new Mock<IScreenCaptureService>();
        captureMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                It.IsAny<ScreenRegion>(),
                It.IsAny<WindowInfo?>(),
                It.IsAny<RegionMode>(),
                It.IsAny<CaptureSourceType>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => captureSupplier());

        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ocrStatus);
        ocrMock.Setup(o => o.StatusMessage).Returns(ocrStatus.ToString());
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] b, CancellationToken ct) => ocrHandler(b, ct));

        var providerMock = new Mock<ITranslationProvider>();
        providerMock.Setup(p => p.ProviderName).Returns("TestTranslator");
        providerMock.Setup(p => p.Status).Returns(ServiceStatus.Connected);
        providerMock.Setup(p => p.StatusMessage).Returns("Ready");
        providerMock.Setup(p => p.TranslateAsync(
                It.IsAny<string>(),
                It.IsAny<SourceLanguage>(),
                It.IsAny<IReadOnlyList<TargetLanguage>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string txt, SourceLanguage src, IReadOnlyList<TargetLanguage> tgts, CancellationToken ct) =>
                transHandler != null
                    ? transHandler(txt, src, tgts, ct)
                    : Task.FromResult(new TranslationResult
                    {
                        OriginalText = txt,
                        DetectedLanguage = "en",
                        Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = $"VI:{txt}" },
                        LatencyMs = 5,
                        ProviderName = "TestTranslator"
                    }));

        var resolverMock = new Mock<ITranslationProviderResolver>();
        resolverMock.Setup(r => r.ResolveActiveProvider()).Returns(providerMock.Object);
        resolverMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(providerMock.Object);
        resolverMock.Setup(r => r.ActiveProviderBadgeText).Returns("TEST");

        var coordinator = new SubtitleCoordinator(
            captureMock.Object,
            ocrMock.Object,
            resolverMock.Object,
            settingsMock.Object,
            changeDetector);

        return (coordinator, ocrMock, providerMock);
    }

    private static byte[] RenderSubtitlePng(string text, byte alpha = 255, byte bgShade = 16, int width = 1280, int height = 180, double fontSize = 30)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(bgShade, (byte)(bgShade + 4), (byte)(bgShade + 10))), null, new Rect(0, 0, width, height));
            if (!string.IsNullOrEmpty(text))
            {
                var ft = new FormattedText(
                    text,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    fontSize,
                    new SolidColorBrush(Color.FromArgb(alpha, 248, 250, 252)),
                    1.0);
                double x = (width - ft.Width) / 2.0;
                double y = (height - ft.Height) / 2.0;
                dc.DrawText(ft, new Point(x, y));
            }
        }
        rtb.Render(dv);

        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task StaticBlankFrames_ShouldStopAfterBoundedRetriesWithoutOcrStorm()
    {
        byte[] staticBlank = RenderSubtitlePng(string.Empty);
        int ocrCalls = 0;

        var (coord, _, _) = CreateHarness(
            () => new CaptureFrameResult { ImageBytes = staticBlank, ErrorKind = CaptureErrorKind.None, CaptureLatencyMs = 1 },
            new FrameChangeDetector(),
            (_, _) =>
            {
                Interlocked.Increment(ref ocrCalls);
                return Task.FromResult(OcrResult.Empty);
            });

        coord.Start();
        await Task.Delay(2100);
        coord.Stop();
        await coord.DisposeAsync();

        ocrCalls.Should().BeLessThanOrEqualTo(3, "static blank frame must allow only 1 initial + max 2 bounded retries");
        coord.FrameSkippedUnchangedCount.Should().BeGreaterThanOrEqualTo(10, "settled blank frames must be skipped cleanly");
    }

    [Fact]
    public async Task EngineErrorState_OnStaticFrames_ShouldBoundRetriesInsteadOfPollingForever()
    {
        byte[] staticFrame = RenderSubtitlePng("Static frame");
        int ocrCalls = 0;

        var (coord, _, _) = CreateHarness(
            () => new CaptureFrameResult { ImageBytes = staticFrame, ErrorKind = CaptureErrorKind.None, CaptureLatencyMs = 1 },
            new FrameChangeDetector(),
            (_, _) =>
            {
                Interlocked.Increment(ref ocrCalls);
                return Task.FromResult(OcrResult.Empty);
            },
            ocrStatus: ServiceStatus.Error);

        coord.Start();
        await Task.Delay(2200);
        coord.Stop();
        await coord.DisposeAsync();

        ocrCalls.Should().BeLessThanOrEqualTo(4, "unready/error OCR engine must not cause infinite 350ms OCR storms on static frames");
        coord.FrameSkippedUnchangedCount.Should().BeGreaterThanOrEqualTo(5);
    }

    [Fact]
    public async Task GradualFadeInSubtitle_ShouldTriggerOnceVisibleWithoutBaselineDrift()
    {
        byte[] blank = RenderSubtitlePng(string.Empty);
        byte[] fadeLow = RenderSubtitlePng("Look behind you!", alpha: 24);
        byte[] fadeMid = RenderSubtitlePng("Look behind you!", alpha: 145);
        byte[] fadeFull = RenderSubtitlePng("Look behind you!", alpha: 255);

        int phase = 0;
        var pubTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (coord, _, _) = CreateHarness(
            () => new CaptureFrameResult
            {
                ImageBytes = Volatile.Read(ref phase) switch
                {
                    0 => blank,
                    1 => fadeLow,
                    2 => fadeMid,
                    _ => fadeFull
                },
                ErrorKind = CaptureErrorKind.None,
                CaptureLatencyMs = 1
            },
            new FrameChangeDetector(),
            (bytes, _) =>
            {
                int p = Volatile.Read(ref phase);
                if (p < 2) return Task.FromResult(OcrResult.Empty);
                return Task.FromResult(new OcrResult { FullText = "Look behind you!", Confidence = 0.96, Lines = [new OcrLine { Text = "Look behind you!", Confidence = 0.96 }] });
            });

        coord.SubtitleUpdated += (_, s) =>
        {
            if (s.HasContent && s.OriginalText.Contains("Look behind you!"))
                pubTcs.TrySetResult(s);
        };

        coord.Start();
        await Task.Delay(250);
        Volatile.Write(ref phase, 1);
        await Task.Delay(250);
        Volatile.Write(ref phase, 2);
        await Task.Delay(250);
        Volatile.Write(ref phase, 3);

        var published = await pubTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coord.Stop();
        await coord.DisposeAsync();

        published.VietnameseText.Should().Contain("Look behind you!");
    }

    [Fact]
    public async Task TransientOcrFailure_ShouldRecoverAutomaticallyOnBoundedRetry()
    {
        byte[] staticTextFrame = RenderSubtitlePng("Try again carefully.");
        int callNo = 0;
        var pubTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (coord, _, _) = CreateHarness(
            () => new CaptureFrameResult { ImageBytes = staticTextFrame, ErrorKind = CaptureErrorKind.None, CaptureLatencyMs = 1 },
            new FrameChangeDetector(),
            (_, _) =>
            {
                int n = Interlocked.Increment(ref callNo);
                if (n == 1)
                {
                    return Task.FromResult(OcrResult.Empty);
                }
                return Task.FromResult(new OcrResult { FullText = "Try again carefully.", Confidence = 0.98, Lines = [new OcrLine { Text = "Try again carefully.", Confidence = 0.98 }] });
            });

        coord.SubtitleUpdated += (_, s) =>
        {
            if (s.HasContent) pubTcs.TrySetResult(s);
        };

        coord.Start();
        var sub = await pubTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(700);
        coord.Stop();
        await coord.DisposeAsync();

        callNo.Should().Be(2, "must recover on retry #1 and stop retrying once subtitle text is obtained");
        coord.OcrEmptyOrFailureRetryCount.Should().Be(1);
        sub.OriginalText.Should().Be("Try again carefully.");
    }

    [Fact]
    public async Task RapidSubtitleBurst_ShouldBoundQueueAndPublishLatestWithoutStaleOverwrite()
    {
        int seq = 0;
        var texts = new[] { "Sub-1", "Sub-2", "Sub-3", "Sub-4", "Sub-5" };
        var latestTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedOrder = new ConcurrentQueue<string>();

        var alwaysChanged = new Mock<IFrameChangeDetector>();
        alwaysChanged.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>()))
            .Returns(() => Volatile.Read(ref seq) <= 5);

        var (coord, _, _) = CreateHarness(
            () =>
            {
                int current = Interlocked.Increment(ref seq);
                int idx = Math.Clamp(current - 1, 0, 4);
                return new CaptureFrameResult { ImageBytes = [(byte)(idx + 1), 1], ErrorKind = CaptureErrorKind.None, CaptureLatencyMs = 1 };
            },
            alwaysChanged.Object,
            (b, _) =>
            {
                int idx = Math.Clamp(b[0] - 1, 0, 4);
                return Task.FromResult(new OcrResult { FullText = texts[idx], Confidence = 0.99, Lines = [new OcrLine { Text = texts[idx], Confidence = 0.99 }] });
            },
            async (txt, _, _, ct) =>
            {
                await Task.Delay(txt == "Sub-5" ? 20 : 120, ct);
                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = $"VI:{txt}" },
                    LatencyMs = 20,
                    ProviderName = "Test"
                };
            });

        coord.SubtitleUpdated += (_, s) =>
        {
            if (s.HasContent)
            {
                publishedOrder.Enqueue(s.OriginalText);
                if (s.OriginalText == "Sub-5") latestTcs.TrySetResult(true);
            }
        };

        coord.Start();
        await latestTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        coord.Stop();
        await coord.DisposeAsync();

        coord.OcrQueueLength.Should().BeLessThanOrEqualTo(1);
        publishedOrder.Should().Contain("Sub-5");
        publishedOrder.ToArray()[^1].Should().Be("Sub-5", "older translations must never overwrite latest subtitle");
    }

    [Fact]
    public async Task MultiLineAndHeavySceneCut_ShouldPreserveMultiLineAndSuppressDuplicate()
    {
        byte[] sceneA = RenderSubtitlePng("First line\nSecond line", bgShade: 16);
        byte[] sceneBCutSameText = RenderSubtitlePng("First line\nSecond line", bgShade: 135);
        byte[] sceneCCutNewText = RenderSubtitlePng("Line A changed\nLine B changed", bgShade: 22);

        int phase = 0;
        var translations = 0;
        var firstTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (coord, _, _) = CreateHarness(
            () => new CaptureFrameResult
            {
                ImageBytes = Volatile.Read(ref phase) switch { 0 => sceneA, 1 => sceneBCutSameText, _ => sceneCCutNewText },
                ErrorKind = CaptureErrorKind.None,
                CaptureLatencyMs = 1
            },
            new FrameChangeDetector(),
            (_, _) =>
            {
                int p = Volatile.Read(ref phase);
                string txt = p < 2 ? "First line\nSecond line" : "Line A changed\nLine B changed";
                return Task.FromResult(new OcrResult { FullText = txt, Confidence = 0.97, Lines = [new OcrLine { Text = txt, Confidence = 0.97 }] });
            },
            (txt, _, _, _) =>
            {
                Interlocked.Increment(ref translations);
                return Task.FromResult(new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = $"VI:{txt}" },
                    LatencyMs = 5,
                    ProviderName = "Test"
                });
            });

        coord.SubtitleUpdated += (_, s) =>
        {
            if (!s.HasContent) return;
            if (s.OriginalText.Contains("First line")) firstTcs.TrySetResult(s);
            if (s.OriginalText.Contains("Line A changed")) lastTcs.TrySetResult(s);
        };

        coord.Start();
        var sub1 = await firstTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sub1.OriginalText.Should().Contain("First line").And.Contain("Second line");

        Volatile.Write(ref phase, 1); // heavy scene cut, same subtitle text -> OCR runs but duplicate suppressed
        await Task.Delay(350);
        Volatile.Write(ref phase, 2); // new multi-line text -> translates & updates
        var sub2 = await lastTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coord.Stop();
        await coord.DisposeAsync();

        coord.DuplicateSuppressedCount.Should().BeGreaterThanOrEqualTo(1);
        translations.Should().Be(2);
        sub2.OriginalText.Should().Contain("Line B changed");
    }

    [Fact]
    public async Task RegionChangeDuringInFlightOcr_ShouldRejectStaleOcrResult()
    {
        var ocrEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstOcr = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRegionPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ocrCall = 0;

        var settings = CreateSettings();
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(settings);
        settingsMock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);
        var capMock = new Mock<IScreenCaptureService>();
        capMock.Setup(c => c.CaptureWithDiagnosticsAsync(It.IsAny<ScreenRegion>(), It.IsAny<WindowInfo?>(), It.IsAny<RegionMode>(), It.IsAny<CaptureSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CaptureFrameResult { ImageBytes = [9, 9, 9], ErrorKind = CaptureErrorKind.None, CaptureLatencyMs = 1 });
        var chMock = new Mock<IFrameChangeDetector>();
        chMock.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>())).Returns(true);
        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Running);
        ocrMock.Setup(o => o.StatusMessage).Returns("Running");
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (byte[] _, CancellationToken ct) =>
            {
                int n = Interlocked.Increment(ref ocrCall);
                if (n == 1)
                {
                    ocrEntered.TrySetResult(true);
                    await releaseFirstOcr.Task;
                    return new OcrResult { FullText = "OLD_REGION_TEXT", Confidence = 0.95, Lines = [new OcrLine { Text = "OLD_REGION_TEXT", Confidence = 0.95 }] };
                }
                return new OcrResult { FullText = "NEW_REGION_TEXT", Confidence = 0.95, Lines = [new OcrLine { Text = "NEW_REGION_TEXT", Confidence = 0.95 }] };
            });
        var provMock = new Mock<ITranslationProvider>();
        provMock.Setup(p => p.ProviderName).Returns("Test");
        provMock.Setup(p => p.Status).Returns(ServiceStatus.Connected);
        provMock.Setup(p => p.StatusMessage).Returns("Ready");
        provMock.Setup(p => p.TranslateAsync(It.IsAny<string>(), It.IsAny<SourceLanguage>(), It.IsAny<IReadOnlyList<TargetLanguage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string t, SourceLanguage _, IReadOnlyList<TargetLanguage> _, CancellationToken _) => new TranslationResult
            {
                OriginalText = t,
                DetectedLanguage = "en",
                Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = $"VI:{t}" },
                LatencyMs = 2,
                ProviderName = "Test"
            });
        var resMock = new Mock<ITranslationProviderResolver>();
        resMock.Setup(r => r.ResolveActiveProvider()).Returns(provMock.Object);
        resMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(provMock.Object);
        resMock.Setup(r => r.ActiveProviderBadgeText).Returns("TEST");

        await using var coordinator = new SubtitleCoordinator(capMock.Object, ocrMock.Object, resMock.Object, settingsMock.Object, chMock.Object);
        var seen = new ConcurrentQueue<string>();
        coordinator.SubtitleUpdated += (_, s) =>
        {
            if (s.HasContent)
            {
                seen.Enqueue(s.OriginalText);
                if (s.OriginalText == "NEW_REGION_TEXT") newRegionPublished.TrySetResult(s);
            }
        };

        coordinator.Start();
        await ocrEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.UpdateSelectedRegion(new ScreenRegion(50, 50, 800, 140));
        releaseFirstOcr.TrySetResult(true);

        var fresh = await newRegionPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Stop();

        coordinator.RejectedStaleCount.Should().BeGreaterThanOrEqualTo(1);
        seen.Should().NotContain("OLD_REGION_TEXT");
        fresh.OriginalText.Should().Be("NEW_REGION_TEXT");
    }

    [Fact]
    public async Task StopAndRestartDuringInFlightOcr_ShouldRejectOldSessionResult()
    {
        var ocrEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstOcr = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newSessionPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        int ocrCall = 0;

        var settings = CreateSettings();
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(settings);
        settingsMock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);
        var capMock = new Mock<IScreenCaptureService>();
        capMock.Setup(c => c.CaptureWithDiagnosticsAsync(It.IsAny<ScreenRegion>(), It.IsAny<WindowInfo?>(), It.IsAny<RegionMode>(), It.IsAny<CaptureSourceType>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CaptureFrameResult { ImageBytes = [7, 7, 7], ErrorKind = CaptureErrorKind.None, CaptureLatencyMs = 1 });
        var chMock = new Mock<IFrameChangeDetector>();
        chMock.Setup(d => d.HasFrameChanged(It.IsAny<byte[]>(), It.IsAny<double>())).Returns(true);
        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Running);
        ocrMock.Setup(o => o.StatusMessage).Returns("Running");
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (byte[] _, CancellationToken ct) =>
            {
                int n = Interlocked.Increment(ref ocrCall);
                if (n == 1)
                {
                    ocrEntered.TrySetResult(true);
                    await releaseFirstOcr.Task;
                    return new OcrResult { FullText = "STALE_SESSION_TEXT", Confidence = 0.95, Lines = [new OcrLine { Text = "STALE_SESSION_TEXT", Confidence = 0.95 }] };
                }
                return new OcrResult { FullText = "RESTARTED_SESSION_TEXT", Confidence = 0.95, Lines = [new OcrLine { Text = "RESTARTED_SESSION_TEXT", Confidence = 0.95 }] };
            });
        var provMock = new Mock<ITranslationProvider>();
        provMock.Setup(p => p.ProviderName).Returns("Test");
        provMock.Setup(p => p.Status).Returns(ServiceStatus.Connected);
        provMock.Setup(p => p.StatusMessage).Returns("Ready");
        provMock.Setup(p => p.TranslateAsync(It.IsAny<string>(), It.IsAny<SourceLanguage>(), It.IsAny<IReadOnlyList<TargetLanguage>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string t, SourceLanguage _, IReadOnlyList<TargetLanguage> _, CancellationToken _) => new TranslationResult
            {
                OriginalText = t,
                DetectedLanguage = "en",
                Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = $"VI:{t}" },
                LatencyMs = 2,
                ProviderName = "Test"
            });
        var resMock = new Mock<ITranslationProviderResolver>();
        resMock.Setup(r => r.ResolveActiveProvider()).Returns(provMock.Object);
        resMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(provMock.Object);
        resMock.Setup(r => r.ActiveProviderBadgeText).Returns("TEST");

        await using var coordinator = new SubtitleCoordinator(capMock.Object, ocrMock.Object, resMock.Object, settingsMock.Object, chMock.Object);
        var seen = new ConcurrentQueue<string>();
        coordinator.SubtitleUpdated += (_, s) =>
        {
            if (s.HasContent)
            {
                seen.Enqueue(s.OriginalText);
                if (s.OriginalText == "RESTARTED_SESSION_TEXT") newSessionPublished.TrySetResult(s);
            }
        };

        coordinator.Start();
        await ocrEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Stop();
        coordinator.Start();
        releaseFirstOcr.TrySetResult(true);

        var restarted = await newSessionPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Stop();

        // Wait briefly for the retiring Session-1 OCR task continuation to resume and record stale rejection
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        while (coordinator.RejectedStaleCount < 1 && waitSw.ElapsedMilliseconds < 1000)
        {
            await Task.Delay(10);
        }

        coordinator.RejectedStaleCount.Should().BeGreaterThanOrEqualTo(1);
        seen.Should().NotContain("STALE_SESSION_TEXT");
        restarted.OriginalText.Should().Be("RESTARTED_SESSION_TEXT");
    }
}
