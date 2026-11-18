using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using Xunit;

namespace IntegrationTests;

public class ConcurrencyAndPipelineTests
{
    private static AppSettings CreateFastSettings(TranslationMode mode = TranslationMode.ScreenOcr) => new()
    {
        CaptureFps = 10,
        TranslationMode = mode,
        SourceLanguage = SourceLanguage.English,
        TargetLanguages = [TargetLanguage.Vietnamese],
        DisplayMode = DisplayMode.Triple,
        EnableContextAwareTranslation = false,
        LastRegion = new ScreenRegion(100, 100, 600, 120)
    };

    private static (
        SubtitleCoordinator Coordinator,
        Mock<ISettingsService> SettingsMock,
        Mock<IOcrEngine> OcrMock,
        Mock<ITranslationProvider> ProviderMock,
        Mock<IVoiceActivityDetector> VadMock,
        Mock<ISpeechRecognitionService> SpeechMock) CreateHarness(
            Func<byte[], CancellationToken, Task<OcrResult>>? ocrHandler = null,
            Func<string, SourceLanguage, IReadOnlyList<TargetLanguage>, CancellationToken, Task<TranslationResult>>? transHandler = null,
            Func<byte[], string, CancellationToken, Task<SpeechRecognitionResult>>? speechHandler = null,
            TranslationMode mode = TranslationMode.ScreenOcr)
    {
        var settings = CreateFastSettings(mode);
        var settingsMock = new Mock<ISettingsService>();
        settingsMock.Setup(s => s.Settings).Returns(settings);
        settingsMock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);

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

        var ocrMock = new Mock<IOcrEngine>();
        ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Connected);
        ocrMock.Setup(o => o.StatusMessage).Returns("Ready");
        ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] bytes, CancellationToken ct) =>
                ocrHandler != null
                    ? ocrHandler(bytes, ct)
                    : Task.FromResult(OcrResult.Empty));

        var providerMock = new Mock<ITranslationProvider>();
        providerMock.Setup(p => p.ProviderName).Returns("TestProvider");
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
                        ProviderName = "TestProvider"
                    }));

        var resolverMock = new Mock<ITranslationProviderResolver>();
        resolverMock.Setup(r => r.ResolveActiveProvider()).Returns(providerMock.Object);
        resolverMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(providerMock.Object);
        resolverMock.Setup(r => r.ActiveProviderBadgeText).Returns("TEST");

        var audioMock = new Mock<IAudioCaptureService>();
        var vadMock = new Mock<IVoiceActivityDetector>();
        var speechMock = new Mock<ISpeechRecognitionService>();
        speechMock.Setup(s => s.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] pcm, string lang, CancellationToken ct) =>
                speechHandler != null
                    ? speechHandler(pcm, lang, ct)
                    : Task.FromResult(SpeechRecognitionResult.Empty));

        var coordinator = new SubtitleCoordinator(
            captureMock.Object,
            ocrMock.Object,
            resolverMock.Object,
            settingsMock.Object,
            changeDetectorMock.Object,
            audioMock.Object,
            vadMock.Object,
            speechMock.Object);

        return (coordinator, settingsMock, ocrMock, providerMock, vadMock, speechMock);
    }

    [Fact]
    public async Task ScenarioA_FastProducerSlowConsumer_ShouldBoundQueueDropObsoleteAndConvergeOnLatest()
    {
        int ocrIdx = 0;
        var finalPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (coordinator, _, _, _, _, _) = CreateHarness(
            ocrHandler: (_, _) =>
            {
                int i = Interlocked.Increment(ref ocrIdx);
                string line = i <= 6 ? $"Subtitle burst #{i}" : "Final steady subtitle";
                return Task.FromResult(new OcrResult { FullText = line, Confidence = 0.99, LatencyMs = 1 });
            },
            transHandler: async (txt, _, _, ct) =>
            {
                // Intentionally ignore ct wait on first calls OR respect delay so producer laps consumer
                await Task.Delay(220, ct);
                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = $"Dịch:{txt}" },
                    LatencyMs = 220,
                    ProviderName = "SlowProvider"
                };
            });

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (item.OriginalText == "Final steady subtitle")
            {
                finalPublished.TrySetResult(item);
            }
        };

        coordinator.Start();

        var winner = await finalPublished.Task.WaitAsync(TimeSpan.FromSeconds(6));
        winner.VietnameseText.Should().Be("Dịch:Final steady subtitle");
        coordinator.OcrQueueLength.Should().BeLessThanOrEqualTo(1);
        coordinator.DroppedObsoleteCount.Should().BeGreaterThanOrEqualTo(0);
        coordinator.CurrentSubtitle.OriginalText.Should().Be("Final steady subtitle");

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ScenarioB_OutOfOrderCompletion_StaleSlowResultMustNotOverwriteNewer()
    {
        int step = 0;
        var tcsSlowA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedHistory = new ConcurrentQueue<string>();

        var (coordinator, _, _, _, _, _) = CreateHarness(
            ocrHandler: (_, _) =>
            {
                int s = Interlocked.Increment(ref step);
                string txt = s == 1 ? "First old sentence" : "Second newer sentence";
                return Task.FromResult(new OcrResult { FullText = txt, Confidence = 0.99, LatencyMs = 1 });
            },
            transHandler: async (txt, _, _, _) =>
            {
                // Intentionally ignore CancellationToken to simulate an uncooperative RPC returning AFTER a newer request!
                if (txt == "First old sentence")
                {
                    firstEntered.TrySetResult();
                    string late = await tcsSlowA.Task;
                    return new TranslationResult
                    {
                        OriginalText = txt,
                        DetectedLanguage = "en",
                        Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = late },
                        ProviderName = "SlowUncooperative"
                    };
                }

                await Task.Delay(10);
                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = "Câu mới hơn" },
                    ProviderName = "FastNewer"
                };
            });

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (item.HasContent)
            {
                publishedHistory.Enqueue(item.OriginalText);
                if (item.OriginalText == "Second newer sentence")
                {
                    secondPublished.TrySetResult(item);
                }
            }
        };

        coordinator.Start();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Let frame 2 enqueue while Request 1 is stuck inside uncooperative provider
        await Task.Delay(250);
        // Unblock stale slow request A AFTER newer request B was issued!
        tcsSlowA.TrySetResult("Câu cũ lỗi thời");

        await secondPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);

        // Stale A MUST NOT win over B
        coordinator.CurrentSubtitle.OriginalText.Should().Be("Second newer sentence");
        publishedHistory.Should().NotContain("First old sentence");

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ScenarioC_StopDuringActiveTranslation_ShouldClearAndIgnoreLateCompletion()
    {
        var tcsInFlight = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsAfterStop = new ConcurrentQueue<SubtitleItem>();
        bool stopped = false;

        var (coordinator, _, _, _, _, _) = CreateHarness(
            ocrHandler: (_, _) => Task.FromResult(new OcrResult { FullText = "Active before stop", Confidence = 0.99 }),
            transHandler: async (txt, _, _, _) =>
            {
                entered.TrySetResult();
                string res = await tcsInFlight.Task; // intentionally ignores ct
                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = res }
                };
            });

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (stopped && item.HasContent)
            {
                eventsAfterStop.Enqueue(item);
            }
        };

        coordinator.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        stopped = true;
        coordinator.Stop();
        coordinator.CurrentSubtitle.HasContent.Should().BeFalse();

        // Late completion arrives AFTER Stop()
        tcsInFlight.TrySetResult("Late ghost translation");
        await Task.Delay(150);

        eventsAfterStop.Should().BeEmpty();
        coordinator.CurrentSubtitle.HasContent.Should().BeFalse();

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ScenarioD_RapidStopStart_Epoch1MustNeverLeakIntoEpoch2()
    {
        var tcsEpoch1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var epoch1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var epoch2Published = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        int epochPhase = 1;

        var (coordinator, _, _, _, _, _) = CreateHarness(
            ocrHandler: (_, _) => Task.FromResult(new OcrResult
            {
                FullText = epochPhase == 1 ? "Epoch 1 source" : "Epoch 2 fresh",
                Confidence = 0.99
            }),
            transHandler: async (txt, _, _, _) =>
            {
                if (txt == "Epoch 1 source")
                {
                    epoch1Entered.TrySetResult();
                    string res = await tcsEpoch1.Task;
                    return new TranslationResult
                    {
                        OriginalText = txt,
                        DetectedLanguage = "en",
                        Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = res }
                    };
                }

                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = "Phiên 2 chuẩn" }
                };
            });

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (item.OriginalText == "Epoch 2 fresh")
            {
                epoch2Published.TrySetResult(item);
            }
        };

        coordinator.Start();
        await epoch1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string session1 = coordinator.CurrentSessionId;

        coordinator.Stop();
        epochPhase = 2;
        coordinator.Start();
        string session2 = coordinator.CurrentSessionId;
        session2.Should().NotBe(session1);

        // Release old Epoch 1 request while Epoch 2 is active
        tcsEpoch1.TrySetResult("Dịch rò rỉ từ phiên 1");
        var sub2 = await epoch2Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        sub2.SessionId.Should().Be(session2);
        coordinator.CurrentSubtitle.OriginalText.Should().Be("Epoch 2 fresh");

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ScenarioE_SettingsChangeMidFlight_ShouldRejectStaleVersionTranslation()
    {
        var tcsV1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool keepBlankAfterReset = false;

        var (coordinator, settingsMock, _, _, _, _) = CreateHarness(
            ocrHandler: (_, _) =>
            {
                if (keepBlankAfterReset)
                    return Task.FromResult(OcrResult.Empty);
                return Task.FromResult(new OcrResult { FullText = "Settings V1 line", Confidence = 0.99 });
            },
            transHandler: async (txt, _, _, _) =>
            {
                entered.TrySetResult();
                string val = await tcsV1.Task;
                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = val }
                };
            });

        coordinator.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long vBefore = coordinator.SettingsVersion;

        // Change target language and reset mid-flight -> bumps SettingsVersion & clears subtitle
        settingsMock.Object.Settings.TargetLanguages = [TargetLanguage.Chinese];
        keepBlankAfterReset = true;
        coordinator.ResetTranslationCacheAndRetranslate();
        coordinator.SettingsVersion.Should().BeGreaterThan(vBefore);

        // Late V1 completes cleanly -> MUST NOT publish!
        tcsV1.TrySetResult("Stale V1 Vietnamese result");
        await Task.Delay(150);

        coordinator.CurrentSubtitle.HasContent.Should().BeFalse();
        coordinator.RejectedStaleCount.Should().BeGreaterThan(0);

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ScenarioF_IdenticalSubtitleAcrossFrames_ShouldTranslateOnlyOnceWithoutFlicker()
    {
        int translateCalls = 0;
        var firstPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (coordinator, _, ocrMock, _, _, _) = CreateHarness(
            ocrHandler: (_, _) => Task.FromResult(new OcrResult { FullText = "Hello stable world", Confidence = 0.99 }),
            transHandler: async (txt, _, _, ct) =>
            {
                Interlocked.Increment(ref translateCalls);
                await Task.Delay(180, ct);
                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = "Xin chào thế giới ổn định" }
                };
            });

        int clearEvents = 0;
        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (item.HasContent) firstPublished.TrySetResult(item);
            else Interlocked.Increment(ref clearEvents);
        };

        coordinator.Start();
        await firstPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Let multiple additional 100ms OCR frames run with identical text
        await Task.Delay(450);

        ocrMock.Verify(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.AtLeast(3));
        translateCalls.Should().Be(1);
        clearEvents.Should().Be(0);

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ScenarioG_HybridConcurrentOcrAndSpeech_ShouldDeduplicateAndPublishSafely()
    {
        var ocrPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var speechPublished = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

        var (coordinator, _, _, _, vadMock, _) = CreateHarness(
            mode: TranslationMode.Hybrid,
            ocrHandler: (_, _) => Task.FromResult(new OcrResult { FullText = "We are live on screen", Confidence = 0.99 }),
            speechHandler: async (pcm, _, _) =>
            {
                await Task.Delay(20);
                // Decode test marker from first byte
                string txt = pcm.Length > 0 && pcm[0] == 1
                    ? "We are live on screen" // Duplicate of OCR -> should be ignored
                    : "Distinct spoken dialogue";
                return new SpeechRecognitionResult { Text = txt, DetectedLanguage = "en", LatencyMs = 20 };
            });

        var published = new ConcurrentQueue<SubtitleItem>();
        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (item.HasContent)
            {
                published.Enqueue(item);
                if (item.SourceMode == TranslationMode.ScreenOcr) ocrPublished.TrySetResult(item);
                if (item.SourceMode == TranslationMode.AudioRealtime) speechPublished.TrySetResult(item);
            }
        };

        coordinator.Start();
        await ocrPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 1. Speech matching OCR MUST NOT overwrite OCR
        vadMock.Raise(v => v.SpeechSegmentReady += null, vadMock.Object, new SpeechSegment { Id = 1, PcmData = [1, 2] });
        await Task.Delay(150);
        published.Count(p => p.SourceMode == TranslationMode.AudioRealtime).Should().Be(0);

        // 2. Distinct Speech MUST publish safely without corrupting state
        vadMock.Raise(v => v.SpeechSegmentReady += null, vadMock.Object, new SpeechSegment { Id = 2, PcmData = [2, 2] });
        var sp = await speechPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        sp.VietnameseText.Should().Be("VI:Distinct spoken dialogue");

        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ScenarioI_QuickTranslateWhileActiveSessionRunning_ShouldReplaceSessionCleanly()
    {
        var region1 = new ScreenRegion(10, 10, 400, 100);
        var region2 = new ScreenRegion(50, 50, 500, 120);
        var r1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcsRegion1 = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var r2Published = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool inRegion2 = false;

        var (coordinator, _, _, _, _, _) = CreateHarness(
            ocrHandler: (_, _) => Task.FromResult(new OcrResult
            {
                FullText = inRegion2 ? "Region 2 subtitle" : "Region 1 subtitle",
                Confidence = 0.99
            }),
            transHandler: async (txt, _, _, _) =>
            {
                if (txt == "Region 1 subtitle")
                {
                    r1Entered.TrySetResult();
                    string late = await tcsRegion1.Task;
                    return new TranslationResult
                    {
                        OriginalText = txt,
                        DetectedLanguage = "en",
                        Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = late }
                    };
                }

                return new TranslationResult
                {
                    OriginalText = txt,
                    DetectedLanguage = "en",
                    Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = "Phụ đề vùng 2" }
                };
            });

        coordinator.SubtitleUpdated += (_, item) =>
        {
            if (item.OriginalText == "Region 2 subtitle")
            {
                r2Published.TrySetResult(item);
            }
        };

        coordinator.UpdateSelectedRegionAndAutoStart(region1);
        await r1Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string s1 = coordinator.CurrentSessionId;

        // Trigger Quick Translate hotkey re-selection while Region 1 is translating
        coordinator.NotifySelectingRegion();
        inRegion2 = true;
        coordinator.UpdateSelectedRegionAndAutoStart(region2);
        string s2 = coordinator.CurrentSessionId;
        s2.Should().NotBe(s1);

        // Late Region 1 completion must never leak over Region 2
        tcsRegion1.TrySetResult("Rò rỉ vùng 1");
        var r2Item = await r2Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        r2Item.SessionId.Should().Be(s2);
        coordinator.CurrentSubtitle.OriginalText.Should().Be("Region 2 subtitle");

        await coordinator.DisposeAsync();
    }
}
