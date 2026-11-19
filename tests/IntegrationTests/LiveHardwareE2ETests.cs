using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FluentAssertions;
using ScreenSub.Audio.Capture;
using ScreenSub.Audio.VAD;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using ScreenSub.Infrastructure;
using ScreenSub.Ocr;
using ScreenSub.Overlay;
using ScreenSub.Speech.Whisper;
using ScreenSub.Translation;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests;

public class LiveHardwareE2ETests
{
    private readonly ITestOutputHelper _output;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public LiveHardwareE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static string ResolveRepoRelative(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {Path.Combine(parts)}");
    }

    [HardwareFact(requiresEdge: true, requiresTranslationModel: true)]
    [Trait("Category", "HardwareE2E")]
    public async Task LiveEdgePlayerWindow_FullPipeline_CaptureOcrEnViT5OverlayAndPauseResumeStop_ShouldWorkEndToEnd()
    {
        string edgeExe = HardwareFactAttribute.EdgeExecutablePath;
        File.Exists(edgeExe).Should().BeTrue("Microsoft Edge must exist for live window E2E test");
        LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk().Should().BeTrue("offline translation models must exist");

        string tempDir = Path.Combine(Path.GetTempPath(), "ScreenSub_EdgeE2E_" + Guid.NewGuid().ToString("N"));
        string userDataDir = Path.Combine(tempDir, "edge_profile");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(userDataDir);

        string htmlPath = Path.Combine(tempDir, "player.html");
        string htmlContent = """
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><title>ScreenSub Live Player QA</title></head>
            <body style="margin:0;background:#0f172a;color:#f8fafc;font-family:'Segoe UI','Microsoft YaHei',sans-serif;overflow:hidden;">
              <div style="width:100vw;height:100vh;display:flex;flex-direction:column;justify-content:flex-end;align-items:center;padding-bottom:26px;box-sizing:border-box;">
                <div id="sub" style="background:rgba(0,0,0,0.72);padding:10px 26px;border-radius:6px;font-size:30px;font-weight:600;text-align:center;">
                  We're just having a drink cleanly.
                </div>
              </div>
              <script>
                const cues = [
                  { ms: 0, text: "We're just having a drink cleanly." },
                  { ms: 2200, text: "Don't get me wrong, I never said that." },
                  { ms: 4200, text: "我们只是喝一杯，别想太多。" },
                  { ms: 6200, text: "Wait a second, let me check the schedule!" }
                ];
                for (const c of cues) {
                  setTimeout(() => { document.getElementById('sub').textContent = c.text; }, c.ms);
                }
              </script>
            </body>
            </html>
            """;
        await File.WriteAllTextAsync(htmlPath, htmlContent);

        Process? edgeProc = null;
        Dispatcher? uiDispatcher = null;
        SubtitleOverlayWindow? overlay = null;
        var overlayReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            uiDispatcher = Dispatcher.CurrentDispatcher;
            overlay = new SubtitleOverlayWindow();
            overlay.EnsureShown();
            overlay.PositionNearRegion(new ScreenRegion(100, 320, 960, 140), ignoreLock: true);
            overlayReady.TrySetResult(true);
            Dispatcher.Run();
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        try
        {
            await overlayReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
            settingsSvc.Settings.CaptureFps = 6;
            settingsSvc.Settings.CaptureSource = CaptureSourceType.Window;
            settingsSvc.Settings.RegionMode = RegionMode.FollowWindow;
            settingsSvc.Settings.TranslationMode = TranslationMode.ScreenOcr;
            settingsSvc.Settings.TranslationEngineMode = TranslationEngineMode.Offline;
            settingsSvc.Settings.SourceLanguage = SourceLanguage.Auto;
            settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];

            string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");

            await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await Task.WhenAll(ocrEngine.InitializeAsync(), transEngine.InitializeAsync());

            // Warm EnViT5 & M2M-100 BEFORE launching timed Edge cues
            await transEngine.TranslateTextAsync("Warmup.", "en", "vi");

            string url = new Uri(htmlPath).AbsoluteUri;
            edgeProc = Process.Start(new ProcessStartInfo
            {
                FileName = edgeExe,
                Arguments = $"--new-window --window-size=960,260 --window-position=80,60 --app=\"{url}\" --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check",
                UseShellExecute = false
            });

            // Wait for Edge window HWND
            nint edgeHwnd = nint.Zero;
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(150);
                foreach (var p in Process.GetProcessesByName("msedge"))
                {
                    if (p.MainWindowHandle != nint.Zero &&
                        p.MainWindowTitle.Contains("ScreenSub Live Player QA", StringComparison.OrdinalIgnoreCase))
                    {
                        edgeHwnd = p.MainWindowHandle;
                        break;
                    }
                }
                if (edgeHwnd != nint.Zero) break;
            }
            edgeHwnd.Should().NotBe(nint.Zero, "Edge player test window must open with a valid HWND");

            GetWindowRect(edgeHwnd, out var rect);
            int capW = Math.Max(400, rect.Right - rect.Left - 40);
            int capH = Math.Min(150, Math.Max(100, (rect.Bottom - rect.Top) - 80));
            var captureRegion = new ScreenRegion(rect.Left + 20, Math.Max(rect.Top + 40, rect.Bottom - capH - 12), capW, capH);
            settingsSvc.Settings.LastRegion = captureRegion;

            var cache = new TranslationCache();
            using var localProvider = new LocalTranslationProvider(transEngine, cache, settingsSvc);
            var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache);
            var mock = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azure, mock);

            var capSvc = new ScreenCaptureService();
            var winInfo = capSvc.GetWindowInfo(edgeHwnd);
            winInfo.Should().NotBeNull();

            var detector = new FrameChangeDetector();
            await using var coordinator = new SubtitleCoordinator(capSvc, ocrEngine, resolver, settingsSvc, detector);
            coordinator.UpdateSelectedWindow(winInfo);
            coordinator.UpdateSelectedRegion(captureRegion);

            var published = new ConcurrentQueue<(string orig, string vi, long capMs, long ocrMs, long trMs, long ovMs, long e2eMs)>();
            var firstCue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lastCue = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            coordinator.SubtitleUpdated += (_, item) =>
            {
                if (!item.HasContent) return;
                var ovSw = Stopwatch.StartNew();
                uiDispatcher!.Invoke(() => overlay!.UpdateSubtitle(item));
                ovSw.Stop();
                long ovMs = Math.Max(ovSw.ElapsedMilliseconds, overlay!.LastUpdateLatencyMs);

                published.Enqueue((
                    item.OriginalText,
                    item.VietnameseText,
                    coordinator.LastCaptureLatencyMs,
                    coordinator.LastOcrLatencyMs,
                    coordinator.LastTranslationLatencyMs,
                    ovMs,
                    coordinator.LastEndToEndLatencyMs + ovMs));

                if (item.OriginalText.Contains("having a drink", StringComparison.OrdinalIgnoreCase))
                    firstCue.TrySetResult(true);
                if (item.OriginalText.Contains("check the schedule", StringComparison.OrdinalIgnoreCase))
                    lastCue.TrySetResult(true);
            };

            coordinator.Start();
            try
            {
                await firstCue.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await lastCue.Task.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException tex)
            {
                var capDiag = coordinator.LastCaptureDiagnostics;
                throw new TimeoutException(
                    $"{tex.Message} [EDGE_DIAG EdgeRect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) CapReg=({captureRegion.X},{captureRegion.Y},{captureRegion.Width}x{captureRegion.Height}) CapBackend={capDiag.BackendName} CapOk={capDiag.IsSuccess} CapErr={capDiag.ErrorKind}:'{capDiag.ErrorMessage}' Bytes={capDiag.ImageBytes?.Length ?? 0} OcrAtt={coordinator.OcrAttemptCount} OcrOk={coordinator.OcrSuccessCount} LastOcr='{coordinator.LastOcrRawText}' Pub={published.Count} Status='{coordinator.PipelineStatusText}']",
                    tex);
            }

            coordinator.Pause();
            coordinator.State.Should().Be(SystemState.Paused);
            long attemptsAtPause = coordinator.OcrAttemptCount;
            await Task.Delay(350);
            coordinator.OcrAttemptCount.Should().Be(attemptsAtPause, "paused coordinator must not run OCR");

            coordinator.Start();
            coordinator.State.Should().Be(SystemState.Translating);

            coordinator.Stop();
            coordinator.State.Should().Be(SystemState.Stopped);
            coordinator.CurrentSubtitle.HasContent.Should().BeFalse();

            var items = published.ToArray();
            foreach (var it in items)
            {
                _output.WriteLine($"[EDGE_GUI_CUE] '{it.orig}' -> '{it.vi}' | cap={it.capMs}ms ocr={it.ocrMs}ms tr={it.trMs}ms ov={it.ovMs}ms total={it.e2eMs}ms");
            }

            var totals = items.Select(x => (double)x.e2eMs).OrderBy(x => x).ToArray();
            double p50 = totals[totals.Length / 2];
            double p95 = totals[(int)(0.95 * (totals.Length - 1))];
            _output.WriteLine($"[EDGE_GUI_SUMMARY] Cues={items.Length}, P50={p50:F0}ms, P95={p95:F0}ms, Crashes={transEngine.WorkerCrashOrResetCount}");

            items.Length.Should().BeGreaterThanOrEqualTo(3, "Edge video cues including EN + ZH must be captured, OCR'd, translated, and rendered onto real Overlay");
            items.Any(i => i.orig.Contains("喝一杯") || i.vi.Contains("uống")).Should().BeTrue();
            items.All(i => !i.vi.Contains("Mock", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
            transEngine.WorkerCrashOrResetCount.Should().Be(0);
        }
        finally
        {
            uiDispatcher?.InvokeShutdown();
            staThread.Join(2000);
            try { edgeProc?.Kill(entireProcessTree: true); } catch { }
            foreach (var p in Process.GetProcessesByName("msedge"))
            {
                try
                {
                    if (p.MainWindowTitle.Contains("ScreenSub Live Player QA", StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [HardwareFact(requiresAudioOutput: true)]
    [Trait("Category", "HardwareE2E")]
    public async Task RealSpokenTtsAudio_PlaybackThroughSpeakers_WasapiHardwareLoopbackAndVad_ShouldEmitSpeechSegment()
    {
        using var wasapi = new WasapiAudioCaptureService();
        var devices = wasapi.GetOutputDevices();
        devices.Count.Should().BeGreaterThan(0, "active WASAPI output device is required");

        string wavPath = Path.Combine(Path.GetTempPath(), $"screensub_tts_{Guid.NewGuid():N}.wav");
        try
        {
            // Generate genuine spoken English audio WAV via built-in Windows offline SAPI voice and play it via Windows audio mixer
            var genPsi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"Add-Type -AssemblyName System.Speech; $s=New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Volume=35; $s.SetOutputToWaveFile('{wavPath}'); $s.Speak('Hello everyone, testing ScreenSub audio loopback.'); $s.Dispose()\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var genProc = Process.Start(genPsi)!)
            {
                await genProc.WaitForExitAsync();
                genProc.ExitCode.Should().Be(0);
            }
            File.Exists(wavPath).Should().BeTrue();

            var vad = new EnergyVoiceActivityDetector(preRollMs: 120)
            {
                SilenceThreshold = 0.004,
                MinSpeechDurationMs = 200,
                HangoverMs = 250
            };

            int dataCallbacks = 0;
            long totalPcmBytes = 0;
            float maxPeak = 0f;
            var segTcs = new TaskCompletionSource<SpeechSegment>(TaskCreationOptions.RunContinuationsAsynchronously);

            wasapi.AudioLevelChanged += (_, lvl) =>
            {
                if (lvl.PeakLevel > maxPeak) maxPeak = lvl.PeakLevel;
            };
            wasapi.AudioDataAvailable += (_, e) =>
            {
                Interlocked.Increment(ref dataCallbacks);
                Interlocked.Add(ref totalPcmBytes, e.Pcm16Data.Length);
                vad.ProcessAudioChunk(e.Pcm16Data, e.SampleRate);
            };
            vad.SpeechSegmentReady += (_, seg) => segTcs.TrySetResult(seg);

            wasapi.Start();
            await Task.Delay(150);

            var playPsi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"$sp=New-Object System.Media.SoundPlayer '{wavPath}'; $sp.PlaySync(); $sp.Dispose()\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var playProc = Process.Start(playPsi)!)
            {
                await playProc.WaitForExitAsync();
                playProc.ExitCode.Should().Be(0);
            }

            // Allow hangover window to close segment cleanly
            var emitted = await segTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            wasapi.Stop();

            _output.WriteLine($"[REAL_AUDIO_LOOPBACK] Device='{devices.FirstOrDefault(d => d.IsDefault)?.Name ?? devices[0].Name}', Callbacks={dataCallbacks}, TotalPcm16kBytes={totalPcmBytes}, MaxPeak={maxPeak:F4}, EmittedDurationMs={emitted.DurationMs}, SegmentBytes={emitted.PcmData.Length}");

            dataCallbacks.Should().BeGreaterThan(0);
            totalPcmBytes.Should().BeGreaterThan(16000);
            maxPeak.Should().BeGreaterThan(0.01f, "WASAPI loopback must capture real acoustic output from default speaker endpoint");
            emitted.PcmData.Length.Should().BeGreaterThan(4000);
            emitted.DurationMs.Should().BeGreaterThanOrEqualTo(200);
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        }
    }

    private static async Task GenerateSpeechWavAsync(string wavPath, string text, int volume = 75, int rate = -1)
    {
        string escapedText = text.Replace("'", "''");
        string escapedPath = wavPath.Replace("'", "''");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"Add-Type -AssemblyName System.Speech; $s=New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Volume={volume}; $s.Rate={rate}; $s.SetOutputToWaveFile('{escapedPath}'); $s.Speak('{escapedText}'); $s.Dispose()\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        proc.ExitCode.Should().Be(0);
    }

    private static async Task PlayWavThroughSpeakersAsync(string wavPath)
    {
        string escapedPath = wavPath.Replace("'", "''");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"$sp=New-Object System.Media.SoundPlayer '{escapedPath}'; $sp.PlaySync(); $sp.Dispose()\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        await proc.WaitForExitAsync();
        proc.ExitCode.Should().Be(0);
    }

    private static string NormalizeForMetric(string text)
    {
        var chars = text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int LevenshteinDistance<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : notnull
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int i = 0; i <= a.Count; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Count; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Count; i++)
        {
            for (int j = 1; j <= b.Count; j++)
            {
                int cost = EqualityComparer<T>.Default.Equals(a[i - 1], b[j - 1]) ? 0 : 1;
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        return dp[a.Count, b.Count];
    }

    private static (double Wer, double Cer) ComputeWerAndCer(string reference, string hypothesis)
    {
        string refNorm = NormalizeForMetric(reference);
        string hypNorm = NormalizeForMetric(hypothesis);
        string[] refWords = refNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] hypWords = hypNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        double wer = refWords.Length == 0 ? 0.0 : (double)LevenshteinDistance(refWords, hypWords) / refWords.Length;
        double cer = refNorm.Length == 0 ? 0.0 : (double)LevenshteinDistance(refNorm.ToCharArray(), hypNorm.ToCharArray()) / refNorm.Length;
        return (wer, cer);
    }

    private static long Percentile(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        double frac = idx - lo;
        return (long)Math.Round(sorted[lo] * (1.0 - frac) + sorted[hi] * frac);
    }

    [HardwareFact(requiresWhisperBase: true)]
    [Trait("Category", "HardwareE2E")]
    public async Task LocalModelManager_WhisperBase_VerifyModelIntegrityAsync_ShouldPassOfficialSha256()
    {
        WhisperModelManager.IsModelAvailable("base").Should().BeTrue("ggml-base.bin must exist for SHA-256 verification");

        string tempDir = Path.Combine(Path.GetTempPath(), $"screensub_whisper_chk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsSvc = new SettingsService(tempDir);
            await settingsSvc.LoadSettingsAsync();

            var mgr = new LocalModelManager(settingsSvc);
            var meta = mgr.GetModel("whisper-base-ggml");
            meta.Should().NotBeNull();

            bool ok = await mgr.VerifyModelIntegrityAsync("whisper-base-ggml");
            ok.Should().BeTrue("downloaded ggml-base.bin must match official ggerganov/whisper.cpp SHA-256");
            meta!.Status.Should().Be(ModelInstallationStatus.Installed);

            string resolvedPath = WhisperModelManager.GetModelPath("base", settingsSvc.Settings.ModelStorageDirectory, settingsSvc.Settings.WhisperModelPath);
            var fi = new FileInfo(resolvedPath);
            fi.Exists.Should().BeTrue();
            fi.Length.Should().Be(meta.SizeBytes);

            _output.WriteLine($"[WHISPER_CHECKSUM] Path='{resolvedPath}', SizeBytes={fi.Length}, ExpectedSha256={meta.Sha256Checksum}, Status={meta.Status}");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [HardwareFact(requiresWhisperBase: true, requiresTranslationModel: true, requiresAudioOutput: true)]
    [Trait("Category", "HardwareE2E")]
    public async Task RealWhisperPipeline_WasapiToVadToWhisperToEnViT5ToOverlay_MultiUtteranceAndStopRestart_ShouldSucceed()
    {
        WhisperModelManager.IsModelAvailable("base").Should().BeTrue("ggml-base.bin must exist");
        LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk().Should().BeTrue("offline translation models must exist");
        using (var checkAudio = new WasapiAudioCaptureService())
        {
            checkAudio.GetOutputDevices().Count.Should().BeGreaterThan(0, "active WASAPI output device is required");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"screensub_whisper_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var wavFiles = new List<string>();
        Dispatcher? uiDispatcher = null;
        SubtitleOverlayWindow? overlay = null;
        var overlayReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            uiDispatcher = Dispatcher.CurrentDispatcher;
            overlay = new SubtitleOverlayWindow();
            overlay.EnsureShown();
            overlay.PositionNearRegion(new ScreenRegion(100, 320, 960, 140), ignoreLock: true);
            overlayReady.TrySetResult(true);
            Dispatcher.Run();
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        try
        {
            await overlayReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var proc = Process.GetCurrentProcess();
            proc.Refresh();
            long baselineWsMb = proc.WorkingSet64 / (1024 * 1024);
            long baselinePrivMb = proc.PrivateMemorySize64 / (1024 * 1024);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
            settingsSvc.Settings.TranslationMode = TranslationMode.AudioRealtime;
            settingsSvc.Settings.TranslationEngineMode = TranslationEngineMode.Offline;
            settingsSvc.Settings.SpeechPerformanceMode = SpeechPerformanceMode.Balanced;
            settingsSvc.Settings.SpeechLanguage = "auto";
            settingsSvc.Settings.SourceLanguage = SourceLanguage.Auto;
            settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];

            await using var speechSvc = new WhisperSpeechRecognitionService(settingsSvc);
            var loadSw = Stopwatch.StartNew();
            await speechSvc.InitializeAsync();
            loadSw.Stop();

            speechSvc.IsModelLoaded.Should().BeTrue($"Whisper model must load cleanly: {speechSvc.StatusMessage}");
            speechSvc.Status.Should().Be(ServiceStatus.Running);

            proc.Refresh();
            long afterWhisperLoadWsMb = proc.WorkingSet64 / (1024 * 1024);
            long afterWhisperLoadPrivMb = proc.PrivateMemorySize64 / (1024 * 1024);

            string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");

            await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await transEngine.InitializeAsync();
            await transEngine.TranslateTextAsync("Warmup.", "en", "vi");

            var cache = new TranslationCache();
            using var localProvider = new LocalTranslationProvider(transEngine, cache, settingsSvc);
            var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache);
            var mock = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azure, mock);

            using var wasapi = new WasapiAudioCaptureService();
            var vad = new EnergyVoiceActivityDetector();

            var capSvc = new ScreenCaptureService();
            var detector = new FrameChangeDetector();
            await using var coordinator = new SubtitleCoordinator(capSvc, ocrEngine, resolver, settingsSvc, detector, wasapi, vad, speechSvc);

            string[] utterances =
            [
                "Good morning everyone and welcome to the live subtitle test.",
                "Please turn left at the next intersection near the station.",
                "The weather forecast shows heavy rain later this evening.",
                "Artificial intelligence translates speech in real time on Windows.",
                "Always verify the security checksum before installing updates.",
                "Thank you for watching our demonstration today."
            ];

            foreach (string u in utterances)
            {
                string wav = Path.Combine(tempDir, $"utt_{wavFiles.Count + 1}.wav");
                await GenerateSpeechWavAsync(wav, u, volume: 75, rate: -1);
                wavFiles.Add(wav);
            }
            string restartWav = Path.Combine(tempDir, "utt_restart.wav");
            const string restartUtterance = "Restart test succeeded cleanly.";
            await GenerateSpeechWavAsync(restartWav, restartUtterance, volume: 75, rate: -1);
            wavFiles.Add(restartWav);

            TaskCompletionSource<SpeechRecognitionResult>? currentSpeechTcs = null;
            TaskCompletionSource<(SubtitleItem item, long overlayMs, long e2eAfterVadMs)>? currentSubTcs = null;
            Stopwatch? segReadySw = null;

            vad.SpeechSegmentReady += (_, _) =>
            {
                segReadySw = Stopwatch.StartNew();
            };

            coordinator.SpeechRecognized += (_, sr) =>
            {
                currentSpeechTcs?.TrySetResult(sr);
            };
            coordinator.SubtitleUpdated += (_, sub) =>
            {
                if (!sub.HasContent)
                {
                    uiDispatcher?.BeginInvoke(() => overlay?.UpdateSubtitle(sub));
                    return;
                }

                var ovSw = Stopwatch.StartNew();
                uiDispatcher?.Invoke(() => overlay!.UpdateSubtitle(sub));
                ovSw.Stop();
                long ovMs = ovSw.ElapsedMilliseconds;
                coordinator.LastOverlayUpdateLatencyMs = ovMs;
                long e2eAfterVadMs = segReadySw?.ElapsedMilliseconds ?? 0;
                currentSubTcs?.TrySetResult((sub, ovMs, e2eAfterVadMs));
            };

            coordinator.SetTranslationMode(TranslationMode.AudioRealtime);
            coordinator.Start();
            coordinator.State.Should().Be(SystemState.Translating);
            await Task.Delay(250);

            var speechLatencies = new List<long>();
            var transLatencies = new List<long>();
            var e2eLatencies = new List<long>();
            var wers = new List<double>();
            var cers = new List<double>();
            long peakWsMb = afterWhisperLoadWsMb;
            long peakPrivMb = afterWhisperLoadPrivMb;

            for (int i = 0; i < utterances.Length; i++)
            {
                currentSpeechTcs = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                currentSubTcs = new TaskCompletionSource<(SubtitleItem, long, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
                segReadySw = null;

                await PlayWavThroughSpeakersAsync(wavFiles[i]);

                var speechRes = await currentSpeechTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var (subRes, ovMs, e2eAfterVadMs) = await currentSubTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

                var (wer, cer) = ComputeWerAndCer(utterances[i], speechRes.Text);
                wers.Add(wer);
                cers.Add(cer);
                speechLatencies.Add(speechRes.LatencyMs);
                transLatencies.Add(coordinator.LastTranslationLatencyMs);
                e2eLatencies.Add(e2eAfterVadMs);

                proc.Refresh();
                peakWsMb = Math.Max(peakWsMb, proc.WorkingSet64 / (1024 * 1024));
                peakPrivMb = Math.Max(peakPrivMb, proc.PrivateMemorySize64 / (1024 * 1024));

                _output.WriteLine($"[WHISPER_UTT_{i + 1}] Ref=\"{utterances[i]}\" | Hyp=\"{speechRes.Text}\" | Lang={speechRes.DetectedLanguage} | WER={wer:P1} | CER={cer:P1} | SpeechMs={speechRes.LatencyMs} | TransMs={coordinator.LastTranslationLatencyMs} | OvMs={ovMs} | VadToOverlayMs={e2eAfterVadMs} | VI=\"{subRes.VietnameseText}\"");

                speechRes.Text.Should().NotBeNullOrWhiteSpace();
                subRes.VietnameseText.Should().NotBeNullOrWhiteSpace();
                subRes.VietnameseText.Should().NotContain("[");
                subRes.SourceMode.Should().Be(TranslationMode.AudioRealtime);

                // Gap exceeding HangoverMs so VAD cleanly settles back to Silence before next utterance
                await Task.Delay(700);
            }

            // Verify Stop -> Start lifecycle cleanly clears and resumes real Whisper audio pipeline
            coordinator.Stop();
            coordinator.State.Should().Be(SystemState.Stopped);
            coordinator.CurrentSubtitle.HasContent.Should().BeFalse();
            await Task.Delay(300);

            currentSpeechTcs = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            currentSubTcs = new TaskCompletionSource<(SubtitleItem, long, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
            segReadySw = null;

            coordinator.Start();
            coordinator.State.Should().Be(SystemState.Translating);
            await Task.Delay(250);

            await PlayWavThroughSpeakersAsync(restartWav);
            var restartSpeech = await currentSpeechTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var (restartSub, restartOvMs, restartE2eMs) = await currentSubTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var (restartWer, restartCer) = ComputeWerAndCer(restartUtterance, restartSpeech.Text);

            _output.WriteLine($"[WHISPER_RESTART] Ref=\"{restartUtterance}\" | Hyp=\"{restartSpeech.Text}\" | Lang={restartSpeech.DetectedLanguage} | WER={restartWer:P1} | CER={restartCer:P1} | SpeechMs={restartSpeech.LatencyMs} | TransMs={coordinator.LastTranslationLatencyMs} | OvMs={restartOvMs} | VadToOverlayMs={restartE2eMs} | VI=\"{restartSub.VietnameseText}\"");

            coordinator.Stop();
            coordinator.State.Should().Be(SystemState.Stopped);
            restartSub.VietnameseText.Should().NotBeNullOrWhiteSpace();

            var sortedSpeech = speechLatencies.OrderBy(x => x).ToList();
            var sortedTrans = transLatencies.OrderBy(x => x).ToList();
            var sortedE2e = e2eLatencies.OrderBy(x => x).ToList();
            double avgWer = wers.Average();
            double avgCer = cers.Average();

            _output.WriteLine(
                $"[WHISPER_SUMMARY] Utterances={utterances.Length} (+1 Restart), Model={speechSvc.CurrentModelName}, LoadMs={loadSw.ElapsedMilliseconds}, " +
                $"AvgWER={avgWer:P2}, AvgCER={avgCer:P2}, " +
                $"SpeechP50={Percentile(sortedSpeech, 0.50)}ms, SpeechP95={Percentile(sortedSpeech, 0.95)}ms, " +
                $"TransP50={Percentile(sortedTrans, 0.50)}ms, TransP95={Percentile(sortedTrans, 0.95)}ms, " +
                $"VadToOverlayP50={Percentile(sortedE2e, 0.50)}ms, VadToOverlayP95={Percentile(sortedE2e, 0.95)}ms, " +
                $"RAM_BaselineWS={baselineWsMb}MB, RAM_AfterLoadWS={afterWhisperLoadWsMb}MB (+{afterWhisperLoadWsMb - baselineWsMb}MB), RAM_PeakWS={peakWsMb}MB, " +
                $"RAM_BaselinePriv={baselinePrivMb}MB, RAM_AfterLoadPriv={afterWhisperLoadPrivMb}MB (+{afterWhisperLoadPrivMb - baselinePrivMb}MB), RAM_PeakPriv={peakPrivMb}MB");

            avgWer.Should().BeLessThan(0.25, "Whisper Base on clean English TTS audio should achieve < 25% WER");
            avgCer.Should().BeLessThan(0.15, "Whisper Base on clean English TTS audio should achieve < 15% CER");
        }
        finally
        {
            uiDispatcher?.InvokeShutdown();
            staThread.Join(2000);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [HardwareFact(requiresEdge: true, requiresWhisperBase: true, requiresTranslationModel: true, requiresAudioOutput: true)]
    [Trait("Category", "HardwareE2E")]
    public async Task RealWhisperHybridMode_OcrAndSpeechConcurrent_ShouldDeduplicateMatchingTextAndTranslateDistinctSpeech()
    {
        string edgeExe = HardwareFactAttribute.EdgeExecutablePath;
        File.Exists(edgeExe).Should().BeTrue("Microsoft Edge must exist");
        WhisperModelManager.IsModelAvailable("base").Should().BeTrue("ggml-base.bin must exist");
        LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk().Should().BeTrue("offline translation models must exist");
        using (var checkAudio = new WasapiAudioCaptureService())
        {
            checkAudio.GetOutputDevices().Count.Should().BeGreaterThan(0, "active WASAPI output device is required");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"screensub_hybrid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string userDataDir = Path.Combine(tempDir, "edge_profile");
        Directory.CreateDirectory(userDataDir);

        const string onScreenText = "Welcome to the station.";
        const string distinctSpokenText = "Please open the window before leaving.";

        string htmlPath = Path.Combine(tempDir, "hybrid_player.html");
        await File.WriteAllTextAsync(htmlPath, $$"""
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"><title>ScreenSub Hybrid QA</title>
<style>
  html, body { margin: 0; padding: 0; width: 100%; height: 100%; background: #0b0f17; display: flex; align-items: flex-end; justify-content: center; font-family: 'Segoe UI', sans-serif; }
  #sub { width: 92%; margin-bottom: 18px; padding: 14px 20px; background: rgba(0,0,0,0.88); color: #ffffff; font-size: 32px; font-weight: 700; text-align: center; border-radius: 8px; }
</style>
</head>
<body><div id="sub">{{onScreenText}}</div></body>
</html>
""");

        Process? edgeProc = null;
        Dispatcher? uiDispatcher = null;
        SubtitleOverlayWindow? overlay = null;
        var overlayReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var staThread = new Thread(() =>
        {
            uiDispatcher = Dispatcher.CurrentDispatcher;
            overlay = new SubtitleOverlayWindow();
            overlay.EnsureShown();
            overlay.PositionNearRegion(new ScreenRegion(100, 320, 960, 140), ignoreLock: true);
            overlayReady.TrySetResult(true);
            Dispatcher.Run();
        });
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();

        try
        {
            await overlayReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
            settingsSvc.Settings.CaptureFps = 4;
            settingsSvc.Settings.CaptureSource = CaptureSourceType.Window;
            settingsSvc.Settings.RegionMode = RegionMode.FollowWindow;
            settingsSvc.Settings.TranslationMode = TranslationMode.Hybrid;
            settingsSvc.Settings.TranslationEngineMode = TranslationEngineMode.Offline;
            settingsSvc.Settings.SpeechPerformanceMode = SpeechPerformanceMode.Balanced;
            settingsSvc.Settings.SpeechLanguage = "auto";
            settingsSvc.Settings.SourceLanguage = SourceLanguage.Auto;
            settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];

            await using var speechSvc = new WhisperSpeechRecognitionService(settingsSvc);
            string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
            await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await Task.WhenAll(speechSvc.InitializeAsync(), ocrEngine.InitializeAsync(), transEngine.InitializeAsync());
            await transEngine.TranslateTextAsync("Warmup.", "en", "vi");

            string matchWav = Path.Combine(tempDir, "match.wav");
            string distinctWav = Path.Combine(tempDir, "distinct.wav");
            await GenerateSpeechWavAsync(matchWav, onScreenText, volume: 75, rate: -1);
            await GenerateSpeechWavAsync(distinctWav, distinctSpokenText, volume: 75, rate: -1);

            string url = new Uri(htmlPath).AbsoluteUri;
            edgeProc = Process.Start(new ProcessStartInfo
            {
                FileName = edgeExe,
                Arguments = $"--new-window --window-size=960,260 --window-position=80,60 --app=\"{url}\" --user-data-dir=\"{userDataDir}\" --no-first-run --no-default-browser-check",
                UseShellExecute = false
            });

            nint edgeHwnd = nint.Zero;
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(150);
                foreach (var p in Process.GetProcessesByName("msedge"))
                {
                    if (p.MainWindowHandle != nint.Zero && p.MainWindowTitle.Contains("ScreenSub Hybrid QA", StringComparison.OrdinalIgnoreCase))
                    {
                        edgeHwnd = p.MainWindowHandle;
                        break;
                    }
                }
                if (edgeHwnd != nint.Zero) break;
            }
            edgeHwnd.Should().NotBe(nint.Zero);

            GetWindowRect(edgeHwnd, out var rect);
            int capW = Math.Max(400, rect.Right - rect.Left - 40);
            int capH = Math.Min(150, Math.Max(100, (rect.Bottom - rect.Top) - 80));
            var captureRegion = new ScreenRegion(rect.Left + 20, Math.Max(rect.Top + 40, rect.Bottom - capH - 12), capW, capH);
            settingsSvc.Settings.LastRegion = captureRegion;

            var cache = new TranslationCache();
            using var localProvider = new LocalTranslationProvider(transEngine, cache, settingsSvc);
            var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache);
            var mock = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azure, mock);

            using var wasapi = new WasapiAudioCaptureService();
            var vad = new EnergyVoiceActivityDetector(preRollMs: 120)
            {
                SilenceThreshold = 0.004,
                MinSpeechDurationMs = 200,
                HangoverMs = 350
            };

            var capSvc = new ScreenCaptureService();
            var winInfo = capSvc.GetWindowInfo(edgeHwnd);
            winInfo.Should().NotBeNull();
            var detector = new FrameChangeDetector();

            await using var coordinator = new SubtitleCoordinator(capSvc, ocrEngine, resolver, settingsSvc, detector, wasapi, vad, speechSvc);
            coordinator.UpdateSelectedWindow(winInfo);
            coordinator.UpdateSelectedRegion(captureRegion);
            coordinator.SetTranslationMode(TranslationMode.Hybrid);

            var ocrSubTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<SpeechRecognitionResult>? speechTcs = null;
            int audioSubtitlePublishesDuringDup = 0;
            bool monitorDup = false;
            TaskCompletionSource<SubtitleItem>? distinctAudioSubTcs = null;

            coordinator.SpeechRecognized += (_, sr) => speechTcs?.TrySetResult(sr);
            coordinator.SubtitleUpdated += (_, sub) =>
            {
                if (!sub.HasContent)
                {
                    uiDispatcher?.BeginInvoke(() => overlay?.UpdateSubtitle(sub));
                    return;
                }
                uiDispatcher?.Invoke(() => overlay!.UpdateSubtitle(sub));
                if (sub.SourceMode == TranslationMode.ScreenOcr)
                {
                    ocrSubTcs.TrySetResult(sub);
                }
                else if (sub.SourceMode == TranslationMode.AudioRealtime)
                {
                    if (monitorDup) Interlocked.Increment(ref audioSubtitlePublishesDuringDup);
                    distinctAudioSubTcs?.TrySetResult(sub);
                }
            };

            coordinator.Start();

            // Phase A: OCR establishes on-screen subtitle, then matching speech plays -> must deduplicate without overwriting OCR subtitle
            var ocrSub = await ocrSubTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            ocrSub.OriginalText.Should().ContainEquivalentOf("Welcome to the station");

            speechTcs = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            monitorDup = true;
            await PlayWavThroughSpeakersAsync(matchWav);
            var dupSpeech = await speechTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(1200);
            monitorDup = false;

            _output.WriteLine($"[HYBRID_DEDUP] OcrOrig=\"{ocrSub.OriginalText}\", OcrVI=\"{ocrSub.VietnameseText}\", MatchingSpeech=\"{dupSpeech.Text}\", AudioPublishesSuppressed={audioSubtitlePublishesDuringDup == 0}");
            dupSpeech.Text.Should().NotBeNullOrWhiteSpace();
            audioSubtitlePublishesDuringDup.Should().Be(0, "Hybrid mode must suppress audio overwrite when OCR already displays the matching subtitle");

            // Phase B: Distinct spoken audio (not matching on-screen OCR) must still transcribe, translate, and update overlay
            speechTcs = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            distinctAudioSubTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
            await PlayWavThroughSpeakersAsync(distinctWav);

            var distinctSpeech = await speechTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var distinctSub = await distinctAudioSubTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

            _output.WriteLine($"[HYBRID_AUDIO] DistinctSpeech=\"{distinctSpeech.Text}\" (Lang={distinctSpeech.DetectedLanguage}, {distinctSpeech.LatencyMs}ms) -> VI=\"{distinctSub.VietnameseText}\"");
            distinctSub.SourceMode.Should().Be(TranslationMode.AudioRealtime);
            distinctSub.VietnameseText.Should().NotBeNullOrWhiteSpace();
            distinctSub.VietnameseText.Should().NotContain("[");

            coordinator.Stop();
        }
        finally
        {
            uiDispatcher?.InvokeShutdown();
            staThread.Join(2000);
            try { edgeProc?.Kill(entireProcessTree: true); } catch { }
            foreach (var p in Process.GetProcessesByName("msedge"))
            {
                try
                {
                    if (p.MainWindowTitle.Contains("ScreenSub Hybrid QA", StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
