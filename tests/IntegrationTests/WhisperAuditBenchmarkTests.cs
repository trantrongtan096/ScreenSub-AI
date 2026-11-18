using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentAssertions;
using Moq;
using ScreenSub.Audio.Capture;
using ScreenSub.Audio.VAD;
using ScreenSub.Capture;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
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

public class WhisperAuditBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public WhisperAuditBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static async Task<byte[]> SynthesizePcm16Mono16kAsync(string text, string? voiceName = null, int rate = 0, int volume = 85)
    {
        string wavPath = Path.Combine(Path.GetTempPath(), $"ws_synth_{Guid.NewGuid():N}.wav");
        try
        {
            string escapedText = text.Replace("'", "''");
            string escapedPath = wavPath.Replace("'", "''");
            string voiceSelect = string.IsNullOrWhiteSpace(voiceName)
                ? string.Empty
                : $"$vn='{voiceName.Replace("'", "''")}'; if (($s.GetInstalledVoices() | ForEach-Object {{ $_.VoiceInfo.Name }}) -contains $vn) {{ $s.SelectVoice($vn) }}; ";
            string script =
                "Add-Type -AssemblyName System.Speech; " +
                "$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                "$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono); " +
                $"{voiceSelect}$s.Volume={volume}; $s.Rate={rate}; " +
                $"$s.SetOutputToWaveFile('{escapedPath}', $fmt); $s.Speak('{escapedText}'); $s.SetOutputToNull(); $s.Dispose()";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            await proc.WaitForExitAsync();
            proc.ExitCode.Should().Be(0);

            byte[] wav = await File.ReadAllBytesAsync(wavPath);
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                string chunkId = Encoding.ASCII.GetString(wav, pos, 4);
                int chunkSize = BitConverter.ToInt32(wav, pos + 4);
                if (chunkId == "data")
                {
                    int len = Math.Min(chunkSize, wav.Length - (pos + 8));
                    byte[] pcm = new byte[len];
                    Buffer.BlockCopy(wav, pos + 8, pcm, 0, len);
                    return pcm;
                }
                pos += 8 + chunkSize + (chunkSize % 2);
            }
            throw new InvalidDataException("Missing WAV data chunk");
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        }
    }

    private static async Task GenerateSpeechWavFileAsync(string wavPath, string text, int volume = 75, int rate = -1)
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

    private static async Task PlayWavAsync(string wavPath)
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

    private static byte[] AddDeterministicNoise(byte[] pcm16, double snrDb, int seed = 42)
    {
        double sigSq = 0;
        int n = pcm16.Length / 2;
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            samples[i] = s;
            sigSq += s * (double)s;
        }
        double sigRms = Math.Sqrt(sigSq / Math.Max(1, n));
        double noiseRmsTarget = sigRms / Math.Pow(10.0, snrDb / 20.0);

        var rng = new Random(seed);
        var outBytes = new byte[pcm16.Length];
        double b0 = 0, b1 = 0, b2 = 0;
        var rawNoise = new double[n];
        double nSq = 0;
        for (int i = 0; i < n; i++)
        {
            double white = rng.NextDouble() * 2.0 - 1.0;
            b0 = 0.997 * b0 + white * 0.029;
            b1 = 0.985 * b1 + white * 0.032;
            b2 = 0.950 * b2 + white * 0.048;
            double pink = b0 + b1 + b2 + white * 0.05;
            rawNoise[i] = pink;
            nSq += pink * pink;
        }
        double rawRms = Math.Sqrt(nSq / Math.Max(1, n));
        double scale = rawRms > 1e-9 ? noiseRmsTarget / rawRms : 0;

        for (int i = 0; i < n; i++)
        {
            int mixed = (int)Math.Round(samples[i] + rawNoise[i] * scale);
            short clamped = (short)Math.Clamp(mixed, short.MinValue, short.MaxValue);
            outBytes[2 * i] = (byte)(clamped & 0xFF);
            outBytes[2 * i + 1] = (byte)((clamped >> 8) & 0xFF);
        }
        return outBytes;
    }

    private static string Norm(string s)
    {
        var arr = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(arr).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int Lev<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : notnull
    {
        var dp = new int[a.Count + 1, b.Count + 1];
        for (int i = 0; i <= a.Count; i++) dp[i, 0] = i;
        for (int j = 0; j <= b.Count; j++) dp[0, j] = j;
        for (int i = 1; i <= a.Count; i++)
            for (int j = 1; j <= b.Count; j++)
                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + (EqualityComparer<T>.Default.Equals(a[i - 1], b[j - 1]) ? 0 : 1));
        return dp[a.Count, b.Count];
    }

    private static (double Wer, double Cer) Score(string reference, string hyp)
    {
        string r = Norm(reference);
        string h = Norm(hyp);
        var rw = r.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hw = h.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (
            rw.Length == 0 ? 0.0 : (double)Lev(rw, hw) / rw.Length,
            r.Length == 0 ? 0.0 : (double)Lev(r.ToCharArray(), h.ToCharArray()) / r.Length);
    }

    private static long Pct(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        return (long)Math.Round(sorted[lo] * (1.0 - (idx - lo)) + sorted[hi] * (idx - lo));
    }

    private static float CalcPcmRms(byte[] pcm16)
    {
        double sum = 0;
        int n = pcm16.Length / 2;
        if (n == 0) return 0f;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            float f = s / 32768f;
            sum += f * f;
        }
        return (float)Math.Sqrt(sum / n);
    }

    [Fact]
    public async Task Audit_All7Stages_SpeechOnsetToOverlay_EndToEndBreakdown_AutoAndEnglish()
    {
        if (!WhisperModelManager.IsModelAvailable("base") || !LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk())
        {
            return;
        }

        using var devProbe = new WasapiAudioCaptureService();
        if (devProbe.GetOutputDevices().Count == 0) return;
        devProbe.Dispose();

        string tempDir = Path.Combine(Path.GetTempPath(), $"ws_7stage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
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
            settingsSvc.Settings.TranslationMode = TranslationMode.AudioRealtime;
            settingsSvc.Settings.TranslationEngineMode = TranslationEngineMode.Offline;
            settingsSvc.Settings.SpeechPerformanceMode = SpeechPerformanceMode.Balanced;
            settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];

            await using var speechSvc = new WhisperSpeechRecognitionService(settingsSvc);
            string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
            await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await transEngine.InitializeAsync();
            await transEngine.TranslateTextAsync("Warmup.", "en", "vi");

            using var wasapi = new WasapiAudioCaptureService();
            var vad = new EnergyVoiceActivityDetector();
            var cache = new TranslationCache();
            using var localProvider = new LocalTranslationProvider(transEngine, cache, settingsSvc);
            var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache);
            var mock = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azure, mock);

            await using var coordinator = new SubtitleCoordinator(new ScreenCaptureService(), ocrEngine, resolver, settingsSvc, new FrameChangeDetector(), wasapi, vad, speechSvc);

            string[] utterances =
            [
                "Good morning everyone and welcome to the live subtitle test.",
                "Please turn left at the next intersection near the station.",
                "The weather forecast shows heavy rain later this evening.",
                "Artificial intelligence translates speech in real time on Windows.",
                "Always verify the security checksum before installing updates.",
                "Thank you for watching our demonstration today."
            ];

            var wavs = new List<string>(utterances.Length);
            for (int i = 0; i < utterances.Length; i++)
            {
                string w = Path.Combine(tempDir, $"u_{i}.wav");
                await GenerateSpeechWavFileAsync(w, utterances[i], volume: 75, rate: -1);
                wavs.Add(w);
            }

            long firstAudioTick = 0;
            long lastVoiceAudioTick = 0;
            long vadSpeechStartTick = 0;
            long vadSpeechEndTick = 0;
            long segReadyTick = 0;
            long whisperDoneTick = 0;
            long whisperLatencyMs = 0;
            long transLatencyMs = 0;
            long overlayDoneTick = 0;

            TaskCompletionSource<SpeechRecognitionResult>? speechTcs = null;
            TaskCompletionSource<SubtitleItem>? subTcs = null;

            wasapi.AudioDataAvailable += (_, e) =>
            {
                if (CalcPcmRms(e.Pcm16Data) >= vad.SilenceThreshold)
                {
                    long t = Stopwatch.GetTimestamp();
                    Interlocked.CompareExchange(ref firstAudioTick, t, 0);
                    Interlocked.Exchange(ref lastVoiceAudioTick, t);
                }
            };
            vad.StateChanged += (_, st) =>
            {
                long t = Stopwatch.GetTimestamp();
                if (st == VadState.SpeechDetected && vadSpeechStartTick == 0)
                    vadSpeechStartTick = t;
                else if (st == VadState.SpeechEnded)
                    vadSpeechEndTick = t;
            };
            vad.SpeechSegmentReady += (_, _) => segReadyTick = Stopwatch.GetTimestamp();
            coordinator.SpeechRecognized += (_, sr) =>
            {
                whisperDoneTick = Stopwatch.GetTimestamp();
                whisperLatencyMs = sr.LatencyMs;
                speechTcs?.TrySetResult(sr);
            };
            coordinator.SubtitleUpdated += (_, sub) =>
            {
                if (!sub.HasContent)
                {
                    uiDispatcher?.BeginInvoke(() => overlay?.UpdateSubtitle(sub));
                    return;
                }
                uiDispatcher?.Invoke(() => overlay!.UpdateSubtitle(sub));
                overlayDoneTick = Stopwatch.GetTimestamp();
                transLatencyMs = coordinator.LastTranslationLatencyMs;
                subTcs?.TrySetResult(sub);
            };

            static long ToMs(long tStart, long tEnd) =>
                (tStart == 0 || tEnd == 0 || tEnd < tStart) ? 0 : (long)Math.Round((tEnd - tStart) * 1000.0 / Stopwatch.Frequency);

            async Task RunProfile(string label, SourceLanguage srcLang, string speechLang)
            {
                settingsSvc.Settings.SourceLanguage = srcLang;
                settingsSvc.Settings.SpeechLanguage = speechLang;
                coordinator.SetTranslationMode(TranslationMode.AudioRealtime);
                coordinator.Start();
                await Task.Delay(350); // Allow async Whisper warmup started by Start()

                var accumList = new List<long>();
                var hangoverList = new List<long>();
                var whisperList = new List<long>();
                var transList = new List<long>();
                var vadToOverlayList = new List<long>();
                var onsetToOverlayList = new List<long>();
                var postSpeechToOverlayList = new List<long>();
                var wers = new List<double>();
                var proc = Process.GetCurrentProcess();
                var cpuBefore = proc.TotalProcessorTime;

                for (int i = 0; i < utterances.Length; i++)
                {
                    Volatile.Write(ref firstAudioTick, 0);
                    Volatile.Write(ref lastVoiceAudioTick, 0);
                    vadSpeechStartTick = 0;
                    vadSpeechEndTick = 0;
                    segReadyTick = 0;
                    whisperDoneTick = 0;
                    whisperLatencyMs = 0;
                    transLatencyMs = 0;
                    overlayDoneTick = 0;

                    speechTcs = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    subTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

                    await PlayWavAsync(wavs[i]);
                    var sr = await speechTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var sub = await subTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

                    long accumMs = ToMs(firstAudioTick, lastVoiceAudioTick);
                    long hangMs = ToMs(lastVoiceAudioTick, segReadyTick);
                    long postSpeechMs = ToMs(lastVoiceAudioTick, overlayDoneTick);
                    long vadToOvMs = ToMs(segReadyTick, overlayDoneTick);
                    long onsetToOvMs = ToMs(firstAudioTick, overlayDoneTick);
                    var (wer, _) = Score(utterances[i], sr.Text);

                    accumList.Add(accumMs);
                    hangoverList.Add(hangMs);
                    whisperList.Add(whisperLatencyMs);
                    transList.Add(transLatencyMs);
                    vadToOverlayList.Add(vadToOvMs);
                    onsetToOverlayList.Add(onsetToOvMs);
                    postSpeechToOverlayList.Add(postSpeechMs);
                    wers.Add(wer);

                    _output.WriteLine($"[7STAGE_{label}_U{i + 1}] AccumMs={accumMs}, HangoverMs={hangMs}, WhisperMs={whisperLatencyMs}, TransMs={transLatencyMs}, VadToOvMs={vadToOvMs}, PostSpeechToOvMs={postSpeechMs}, OnsetToOvMs={onsetToOvMs} | WER={wer:P1} | Hyp=\"{sr.Text}\" | VI=\"{sub.VietnameseText}\"");
                    await Task.Delay(700);
                }

                coordinator.Stop();
                proc.Refresh();
                double cpuSec = (proc.TotalProcessorTime - cpuBefore).TotalSeconds;
                var sAccum = accumList.OrderBy(x => x).ToList();
                var sHang = hangoverList.OrderBy(x => x).ToList();
                var sWhisp = whisperList.OrderBy(x => x).ToList();
                var sTrans = transList.OrderBy(x => x).ToList();
                var sVadOv = vadToOverlayList.OrderBy(x => x).ToList();
                var sPostOv = postSpeechToOverlayList.OrderBy(x => x).ToList();
                var sOnsetOv = onsetToOverlayList.OrderBy(x => x).ToList();

                _output.WriteLine(
                    $"[7STAGE_SUMMARY_{label}] AvgWER={wers.Average():P2} | " +
                    $"AccumP50={Pct(sAccum, 0.5)}ms | HangoverP50={Pct(sHang, 0.5)}ms | " +
                    $"WhisperP50={Pct(sWhisp, 0.5)}ms P95={Pct(sWhisp, 0.95)}ms | " +
                    $"TransP50={Pct(sTrans, 0.5)}ms P95={Pct(sTrans, 0.95)}ms | " +
                    $"VadToOvP50={Pct(sVadOv, 0.5)}ms P95={Pct(sVadOv, 0.95)}ms | " +
                    $"PostSpeechToOvP50={Pct(sPostOv, 0.5)}ms P95={Pct(sPostOv, 0.95)}ms | " +
                    $"OnsetToOvP50={Pct(sOnsetOv, 0.5)}ms P95={Pct(sOnsetOv, 0.95)}ms | " +
                    $"CpuTime={cpuSec:F2}s, WS={proc.WorkingSet64 / (1024 * 1024)}MB, Priv={proc.PrivateMemorySize64 / (1024 * 1024)}MB");
            }

            await RunProfile("AUTO", SourceLanguage.Auto, "auto");
            await RunProfile("EN_EXPLICIT", SourceLanguage.English, "auto");
        }
        finally
        {
            uiDispatcher?.InvokeShutdown();
            staThread.Join(2000);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task QualityBenchmark_52Utterances_OptimizedWhisperService_CleanVsNoisy()
    {
        if (!WhisperModelManager.IsModelAvailable("base")) return;

        string tempDir = Path.Combine(Path.GetTempPath(), $"ws_q52_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsSvc = new SettingsService(tempDir);
            await settingsSvc.LoadSettingsAsync();
            await using var speechSvc = new WhisperSpeechRecognitionService(settingsSvc);
            await speechSvc.InitializeAsync();
            speechSvc.IsModelLoaded.Should().BeTrue();

            var specs = new List<(string Category, string Voice, int Rate, double SnrDb, string Text)>
            {
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "Good morning everyone, did you get a chance to review my notes?"),
                ("Clean_Conv", "Microsoft Zira Desktop", 0, 99, "Yes, I read everything carefully and added two small suggestions."),
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "Let's grab a quick cup of coffee before the afternoon meeting starts."),
                ("Clean_Conv", "Microsoft Zira Desktop", 0, 99, "Don't forget to bring your umbrella because it looks cloudy outside."),
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "Where did we leave the spare keys for the storage cabinet?"),
                ("Clean_Conv", "Microsoft Zira Desktop", 0, 99, "They are sitting right next to the printer on the second floor."),
                ("Clean_Conv", "Microsoft Zira Desktop", 0, 99, "Thank you so much, I appreciate your help with that."),
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "We should probably wait another ten minutes for the rest of the team."),
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "Sounds good to me, let me finish sending this email first."),
                ("Clean_Conv", "Microsoft Zira Desktop", 0, 99, "Have you ever visited Seattle during the summer months?"),
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "I flew there last July and the weather was surprisingly sunny."),
                ("Clean_Conv", "Microsoft Zira Desktop", 0, 99, "Please make sure the front door is locked before heading home."),
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "What time does the evening train arrive at the central station?"),
                ("Clean_Conv", "Microsoft David Desktop", 0, 99, "It usually pulls into platform four around quarter past seven."),

                ("Short", "Microsoft Zira Desktop", 0, 99, "Hold on."),
                ("Short", "Microsoft David Desktop", 0, 99, "Wait a second!"),
                ("Short", "Microsoft Zira Desktop", 0, 99, "Look behind you!"),
                ("Short", "Microsoft Zira Desktop", 0, 99, "No problem at all."),
                ("Short", "Microsoft David Desktop", 0, 99, "See you tomorrow morning."),
                ("Short", "Microsoft David Desktop", 0, 99, "Are you serious?"),

                ("Long", "Microsoft David Desktop", -1, 99, "When the automated deployment finishes, please check all dashboard metrics before notifying the customer support team."),
                ("Long", "Microsoft Zira Desktop", -1, 99, "Even though the highway traffic looked heavy earlier, we managed to arrive before the conference keynote began."),
                ("Long", "Microsoft David Desktop", -1, 99, "Whenever you edit configuration files on production servers, remember to keep a verified backup copy first."),
                ("Long", "Microsoft David Desktop", -1, 99, "Our new real-time subtitle engine processes screen capture and loopback audio locally without sending private data online."),
                ("Long", "Microsoft Zira Desktop", -1, 99, "Please tell the project manager that both hardware endurance tests and regression suites completed without any crashes."),
                ("Long", "Microsoft Zira Desktop", -1, 99, "Although the rain continued through midnight, the outdoor festival resumed right on schedule early Sunday morning."),

                ("Fast_Speech", "Microsoft Zira Desktop", 2, 99, "Quickly switch to the backup server before the primary connection times out."),
                ("Fast_Speech", "Microsoft David Desktop", 2, 99, "I didn't expect the presentation to finish twenty minutes ahead of schedule."),
                ("Fast_Speech", "Microsoft Zira Desktop", 2, 99, "Check the live transcript carefully and let me know if any words look missing."),
                ("Slow_Speech", "Microsoft David Desktop", -2, 99, "Always verify the cryptographic checksum before installing any software update."),
                ("Slow_Speech", "Microsoft Zira Desktop", -2, 99, "Keep your microphone muted whenever background music is playing nearby."),
                ("Slow_Speech", "Microsoft David Desktop", -2, 99, "Carefully align the capture window with the bottom third of your monitor."),

                ("Tech_Proper", "Microsoft David Desktop", -1, 99, "Deploy the PostgreSQL microservice container to Kubernetes after running GitHub Actions."),
                ("Tech_Proper", "Microsoft Zira Desktop", -1, 99, "Enable DirectML acceleration on NVIDIA GeForce GPUs for faster local inference."),
                ("Tech_Proper", "Microsoft David Desktop", -1, 99, "We configured OAuth authentication tokens using Microsoft Azure Active Directory."),
                ("Tech_Proper", "Microsoft David Desktop", -1, 99, "ScreenSub uses CTranslate2 quantization and Whisper GGML models on Windows eleven."),
                ("Tech_Proper", "Microsoft Zira Desktop", -1, 99, "Dr. Tanaka flew from Tokyo to San Francisco via London Heathrow Airport."),
                ("Tech_Proper", "Microsoft Zira Desktop", -1, 99, "Always validate SHA256 hashes against Hugging Face LFS metadata before loading."),
                ("Tech_Proper", "Microsoft David Desktop", -1, 99, "Connect the WASAPI loopback capture stream into the 16 kilohertz resampler."),
                ("Tech_Proper", "Microsoft Zira Desktop", -1, 99, "Open Visual Studio Code and inspect the async cancellation token handler."),

                ("Noisy_18dB", "Microsoft David Desktop", 0, 18, "Can you hear me clearly despite the background noise in this room?"),
                ("Noisy_18dB", "Microsoft Zira Desktop", 0, 18, "Yes, your voice comes through cleanly on the speaker loopback."),
                ("Noisy_18dB", "Microsoft Zira Desktop", 0, 18, "Please lower the game volume slightly so dialogue stays easy to follow."),
                ("Noisy_18dB", "Microsoft David Desktop", -1, 18, "The detective walked across the rainy street toward the corner diner."),
                ("Noisy_18dB", "Microsoft Zira Desktop", 0, 18, "We need two more tickets for tonight's cinema screening at eight."),
                ("Noisy_18dB", "Microsoft David Desktop", 0, 18, "Watch out for the broken step near the entrance of the building."),
                ("Noisy_12dB", "Microsoft David Desktop", 0, 12, "Turn left after the bridge and follow the signs to downtown."),
                ("Noisy_12dB", "Microsoft Zira Desktop", 0, 12, "Everyone should evacuate the building immediately when the alarm rings."),
                ("Noisy_12dB", "Microsoft Zira Desktop", 0, 12, "Sorry, I missed your call because my phone was on silent mode."),
                ("Noisy_12dB", "Microsoft David Desktop", -1, 12, "Never share your private recovery key with anyone on the internet."),
                ("Noisy_12dB", "Microsoft Zira Desktop", 0, 12, "Let's review yesterday's benchmark report during our morning standup."),
                ("Noisy_12dB", "Microsoft David Desktop", 0, 12, "That documentary about deep sea exploration was truly fascinating.")
            };

            specs.Count.Should().Be(52);
            var items = new List<(string Category, string Text, byte[] Pcm)>(52);
            foreach (var sp in specs)
            {
                byte[] pcm = await SynthesizePcm16Mono16kAsync(sp.Text, sp.Voice, sp.Rate, volume: 85);
                if (sp.SnrDb < 90) pcm = AddDeterministicNoise(pcm, sp.SnrDb, seed: sp.Text.Length * 31);
                items.Add((sp.Category, sp.Text, pcm));
            }

            async Task EvalProd(string langParam)
            {
                await speechSvc.RecognizeAsync(items[0].Pcm, langParam); // warm
                var lat = new List<long>();
                var wersClean = new List<double>();
                var wersNoisy = new List<double>();
                var wersAll = new List<double>();
                var cersAll = new List<double>();
                int langOk = 0;

                foreach (var it in items)
                {
                    var res = await speechSvc.RecognizeAsync(it.Pcm, langParam);
                    var (wer, cer) = Score(it.Text, res.Text);
                    lat.Add(res.LatencyMs);
                    wersAll.Add(wer);
                    cersAll.Add(cer);
                    if (it.Category.StartsWith("Noisy", StringComparison.Ordinal)) wersNoisy.Add(wer);
                    else wersClean.Add(wer);
                    if (res.DetectedLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase)) langOk++;
                }

                var sorted = lat.OrderBy(x => x).ToList();
                _output.WriteLine($"[PROD52_{langParam.ToUpperInvariant()}] Count=52 | CleanWER={wersClean.Average():P2}, NoisyWER={wersNoisy.Average():P2}, AllWER={wersAll.Average():P2}, AllCER={cersAll.Average():P2} | LangAcc={(double)langOk / 52:P1} | SpeechP50={Pct(sorted, 0.5)}ms P95={Pct(sorted, 0.95)}ms");
                wersAll.Average().Should().BeLessThan(0.10);
            }

            await EvalProd("auto");
            await EvalProd("en");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Vad_ContinuousSpeechOverMaxDuration_ShouldChunkCleanlyWithoutFragmenting()
    {
        var vad = new EnergyVoiceActivityDetector(preRollMs: 300)
        {
            SilenceThreshold = 0.012,
            MinSpeechDurationMs = 300,
            MaxSpeechDurationMs = 6000,
            HangoverMs = 500
        };

        var segments = new List<SpeechSegment>();
        vad.SpeechSegmentReady += (_, seg) => segments.Add(seg);

        // Build 20ms active tone chunk (amplitude ~0.1 -> RMS ~0.07 >> 0.012)
        byte[] speechChunk = new byte[640];
        for (int i = 0; i < 320; i++)
        {
            short val = (short)(3000 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
            speechChunk[2 * i] = (byte)(val & 0xFF);
            speechChunk[2 * i + 1] = (byte)((val >> 8) & 0xFF);
        }
        byte[] silenceChunk = new byte[640];

        // Feed 9 seconds of continuous voice (450 x 20ms) + 600ms silence
        for (int i = 0; i < 450; i++) vad.ProcessAudioChunk(speechChunk, 16000);
        for (int i = 0; i < 30; i++) vad.ProcessAudioChunk(silenceChunk, 16000);

        // Must yield exactly 2 clean macro segments (~6s first, ~3s trailing) — NOT dozens of 20ms shards!
        segments.Count.Should().Be(2);
        segments[0].DurationMs.Should().BeGreaterThanOrEqualTo(5500);
        segments[1].DurationMs.Should().BeGreaterThanOrEqualTo(2500);
    }

    private static byte[] RenderBlankPng(int width = 1280, int height = 180)
    {
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 20, 26)), null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);
        using var ms = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task FrameChangeStaticBlankFrames_StressReproUnderCpuLoad_ShouldNeverExceedRetryBound()
    {
        byte[] staticBlank = RenderBlankPng();
        using var cpuCts = new CancellationTokenSource();
        var cpuLoaders = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            double x = 1.0001;
            while (!cpuCts.IsCancellationRequested)
            {
                for (int i = 0; i < 50_000; i++) x = (x * 1.000001) % 1000.0;
            }
        })).ToArray();

        int anomalies = 0;
        try
        {
            for (int iter = 0; iter < 15; iter++)
            {
                var settings = new AppSettings
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
                var settingsMock = new Mock<ISettingsService>();
                settingsMock.Setup(s => s.Settings).Returns(settings);
                settingsMock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);

                var capMock = new Mock<IScreenCaptureService>();
                capMock.Setup(c => c.CaptureWithDiagnosticsAsync(
                        It.IsAny<ScreenRegion>(), It.IsAny<WindowInfo?>(),
                        It.IsAny<RegionMode>(), It.IsAny<CaptureSourceType>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new CaptureFrameResult { ImageBytes = staticBlank, ErrorKind = CaptureErrorKind.None, CaptureLatencyMs = 1 });

                int ocrCalls = 0;
                var ocrMock = new Mock<IOcrEngine>();
                ocrMock.Setup(o => o.Status).Returns(ServiceStatus.Running);
                ocrMock.Setup(o => o.StatusMessage).Returns("Running");
                ocrMock.Setup(o => o.RecognizeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        Interlocked.Increment(ref ocrCalls);
                        return Task.FromResult(OcrResult.Empty);
                    });

                var provMock = new Mock<ITranslationProvider>();
                provMock.Setup(p => p.ProviderName).Returns("Test");
                provMock.Setup(p => p.Status).Returns(ServiceStatus.Connected);
                provMock.Setup(p => p.StatusMessage).Returns("Ready");
                var resMock = new Mock<ITranslationProviderResolver>();
                resMock.Setup(r => r.ResolveActiveProvider()).Returns(provMock.Object);
                resMock.Setup(r => r.GetProvider(It.IsAny<TranslationEngineMode>())).Returns(provMock.Object);
                resMock.Setup(r => r.ActiveProviderBadgeText).Returns("TEST");

                var detector = new FrameChangeDetector();
                await using var coord = new SubtitleCoordinator(capMock.Object, ocrMock.Object, resMock.Object, settingsMock.Object, detector);
                coord.Start();
                await Task.Delay(1500);
                coord.Stop();

                if (ocrCalls > 3) anomalies++;
            }
        }
        finally
        {
            cpuCts.Cancel();
            await Task.WhenAll(cpuLoaders);
        }

        _output.WriteLine($"[FLAKY_REPRO_AFTER_FIX] Anomalies (>3 calls) = {anomalies} / 15");
        anomalies.Should().Be(0);
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
}
