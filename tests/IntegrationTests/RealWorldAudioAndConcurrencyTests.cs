using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
using Whisper.net;
using Xunit;
using Xunit.Abstractions;

namespace IntegrationTests;

public class RealWorldAudioAndConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    public RealWorldAudioAndConcurrencyTests(ITestOutputHelper output)
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

    private static long Pct(IReadOnlyList<long> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        return (long)Math.Round(sorted[lo] * (1.0 - (idx - lo)) + sorted[hi] * (idx - lo));
    }

    private static string NormEn(string s)
    {
        var arr = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(arr).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormZh(string s, bool toSimplified)
    {
        string src = toSimplified ? LocalChineseConverter.ToSimplified(s) : s;
        var chars = src.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray();
        return new string(chars);
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

    private static (double Wer, double Cer) ScoreEn(string reference, string hyp)
    {
        string r = NormEn(reference);
        string h = NormEn(hyp);
        var rw = r.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hw = h.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (
            rw.Length == 0 ? 0.0 : (double)Lev(rw, hw) / rw.Length,
            r.Length == 0 ? 0.0 : (double)Lev(r.ToCharArray(), h.ToCharArray()) / r.Length);
    }

    private static (double RawCer, double SimpCer) ScoreZh(string reference, string hyp)
    {
        string rRaw = NormZh(reference, toSimplified: false);
        string hRaw = NormZh(hyp, toSimplified: false);
        string rSimp = NormZh(reference, toSimplified: true);
        string hSimp = NormZh(hyp, toSimplified: true);

        double rawCer = rRaw.Length == 0 ? 0.0 : (double)Lev(rRaw.ToCharArray(), hRaw.ToCharArray()) / rRaw.Length;
        double simpCer = rSimp.Length == 0 ? 0.0 : (double)Lev(rSimp.ToCharArray(), hSimp.ToCharArray()) / rSimp.Length;
        return (rawCer, simpCer);
    }

    private static MemoryStream WrapPcmAsWav(byte[] pcm16Mono16k)
    {
        var ms = new MemoryStream(44 + pcm16Mono16k.Length);
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("RIFF"u8);
            w.Write(36 + pcm16Mono16k.Length);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(16000);
            w.Write(32000);
            w.Write((short)2);
            w.Write((short)16);
            w.Write("data"u8);
            w.Write(pcm16Mono16k.Length);
            w.Write(pcm16Mono16k);
        }
        ms.Position = 0;
        return ms;
    }

    private static byte[] AddBackgroundMusicAndNoise(byte[] pcm16, double snrDb, int seed)
    {
        int n = pcm16.Length / 2;
        var samples = new double[n];
        double sigSq = 0;
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            samples[i] = s;
            sigSq += s * (double)s;
        }
        double sigRms = Math.Sqrt(sigSq / Math.Max(1, n));
        double bgRmsTarget = sigRms / Math.Pow(10.0, snrDb / 20.0);

        var rng = new Random(seed);
        var bg = new double[n];
        double b0 = 0, b1 = 0;
        double bgSq = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / 16000.0;
            // Warm background musical pad (A minor 7th soft synth chord + ambient room noise)
            double chord = 0.35 * Math.Sin(2 * Math.PI * 220.0 * t)
                         + 0.25 * Math.Sin(2 * Math.PI * 261.63 * t)
                         + 0.20 * Math.Sin(2 * Math.PI * 329.63 * t)
                         + 0.15 * Math.Sin(2 * Math.PI * 392.00 * t);
            double white = rng.NextDouble() * 2.0 - 1.0;
            b0 = 0.992 * b0 + white * 0.04;
            b1 = 0.960 * b1 + white * 0.06;
            bg[i] = chord * 0.6 + (b0 + b1) * 0.4;
            bgSq += bg[i] * bg[i];
        }
        double rawRms = Math.Sqrt(bgSq / Math.Max(1, n));
        double scale = rawRms > 1e-9 ? bgRmsTarget / rawRms : 0;

        var outBytes = new byte[pcm16.Length];
        for (int i = 0; i < n; i++)
        {
            short v = (short)Math.Clamp((int)Math.Round(samples[i] + bg[i] * scale), short.MinValue, short.MaxValue);
            outBytes[2 * i] = (byte)(v & 0xFF);
            outBytes[2 * i + 1] = (byte)((v >> 8) & 0xFF);
        }
        return outBytes;
    }

    private static async Task<List<(string Tag, string Speaker, string RefText, byte[] PcmData)>> CaptureEdgeNeuralSpeechUtterancesAsync(
        string edgeExe,
        string tempDir,
        IReadOnlyList<(string Tag, string LangPrefix, string VoiceKeyword, double Rate, string Text)> items,
        ITestOutputHelper output)
    {
        using var wasapi = new WasapiAudioCaptureService();
        var vad = new EnergyVoiceActivityDetector(preRollMs: 300)
        {
            SilenceThreshold = 0.010,
            MinSpeechDurationMs = 280,
            MaxSpeechDurationMs = 9000,
            HangoverMs = 750
        };

        var segChannel = System.Threading.Channels.Channel.CreateUnbounded<SpeechSegment>();
        wasapi.AudioDataAvailable += (_, e) => vad.ProcessAudioChunk(e.Pcm16Data, e.SampleRate);
        vad.SpeechSegmentReady += (_, seg) => segChannel.Writer.TryWrite(seg);

        string htmlPath = Path.Combine(tempDir, "edge_batch_speaker.html");
        string profileDir = Path.Combine(tempDir, "edge_batch_prof");
        Directory.CreateDirectory(profileDir);

        var jsItems = new StringBuilder();
        jsItems.AppendLine("[");
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            string t = it.Text.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string v = it.VoiceKeyword.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string l = it.LangPrefix.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string r = it.Rate.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            jsItems.AppendLine($"  {{ lang: \"{l}\", voice: \"{v}\", rate: {r}, text: \"{t}\" }}{(i + 1 < items.Count ? "," : "")}");
        }
        jsItems.AppendLine("]");

        await File.WriteAllTextAsync(htmlPath, $$"""
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>EDGE_BATCH_WAIT</title></head>
<body><script>
const queue = {{jsItems}};
let started = false;
let idx = 0;

function speakNext() {
  if (idx >= queue.length) {
    document.title = "EDGE_BATCH_ALL_DONE";
    return;
  }
  const item = queue[idx];
  const voices = speechSynthesis.getVoices();
  let v = voices.find(x => x.lang.startsWith(item.lang) && x.name.includes(item.voice))
       || voices.find(x => x.lang.startsWith(item.lang));
  const u = new SpeechSynthesisUtterance(item.text);
  if (v) u.voice = v;
  u.rate = item.rate;
  u.onstart = () => { document.title = "EDGE_BATCH_SPEAKING_" + idx; };
  u.onend = () => {
    idx++;
    setTimeout(speakNext, 1650);
  };
  u.onerror = () => {
    idx++;
    setTimeout(speakNext, 1650);
  };
  speechSynthesis.speak(u);
}

function tryStart() {
  if (started) return;
  const voices = speechSynthesis.getVoices();
  if (!voices || voices.length < 4) return;
  started = true;
  setTimeout(speakNext, 350);
}
speechSynthesis.onvoiceschanged = tryStart;
setTimeout(tryStart, 500);
setTimeout(tryStart, 1500);
</script></body></html>
""", Encoding.UTF8);

        wasapi.Start();
        await Task.Delay(250);

        var captured = new List<(string Tag, string Speaker, string RefText, byte[] PcmData)>(items.Count);
        string url = new Uri(htmlPath).AbsoluteUri;
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = edgeExe,
            Arguments = $"--new-window --window-size=480,200 --window-position=80,80 --app=\"{url}\" --user-data-dir=\"{profileDir}\" --autoplay-policy=no-user-gesture-required --no-first-run --no-default-browser-check",
            UseShellExecute = false
        });

        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var seg = await segChannel.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(22));
                var item = items[i];
                captured.Add((item.Tag, item.VoiceKeyword, item.Text, seg.PcmData));
                output.WriteLine($"[CAPTURED_NEURAL_AUDIO_{i + 1}] Tag={item.Tag}, Voice={item.VoiceKeyword}, DurationMs={seg.DurationMs:F0}, Bytes={seg.PcmData.Length}, Text=\"{item.Text}\"");
            }
        }
        finally
        {
            wasapi.Stop();
            try { proc?.Kill(entireProcessTree: true); } catch { }
            foreach (var p in Process.GetProcessesByName("msedge"))
            {
                try
                {
                    if (p.MainWindowTitle.StartsWith("EDGE_BATCH_", StringComparison.Ordinal))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
        }

        return captured;
    }

    [Fact]
    public async Task Phase2_RealEnglishMultiSpeakerDialogue_AutoVsEnglish_And_WithNoContextVsCarryContext_ShouldValidateQualityAndLatency()
    {
        string edgeExe = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        if (!File.Exists(edgeExe) || !WhisperModelManager.IsModelAvailable("base") || !LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk())
        {
            return;
        }

        using (var devCheck = new WasapiAudioCaptureService())
        {
            if (devCheck.GetOutputDevices().Count == 0) return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"rw_en_qa_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 12 real neural voice utterances across 4 distinct natural speakers (Ava, Andrew, Emma, Brian):
            // - Multi-speaker natural dialogue & short/long sentences
            // - Fast rate (1.25x)
            // - Connected 6-turn contextual technical/proper-noun conversation
            var script = new List<(string Tag, string LangPrefix, string VoiceKeyword, double Rate, string Text)>
            {
                ("Conv_Natural", "en-US", "Ava", 1.00, "Hey Andrew, did the audio capture module pass all stability checks this morning?"),
                ("Conv_Natural", "en-US", "Andrew", 1.00, "Yes, the loopback buffer stayed completely stable without dropping a single frame."),
                ("Short", "en-US", "Emma", 1.00, "Wait right there!"),
                ("Fast_Rate", "en-US", "Brian", 1.25, "We need to switch over to the backup gateway before the live broadcast begins."),
                ("Long_Clause", "en-US", "Ava", 1.00, "Even when background music is playing during an action scene, the voice activity detector isolates spoken dialogue cleanly."),
                ("Noisy_BGM", "en-US", "Andrew", 1.00, "Please turn down the soundtrack slightly so everyone can hear the speaker clearly."),

                // 6-turn connected contextual dialogue with recurring proper nouns & technical terms
                ("Context_1", "en-US", "Emma", 1.00, "Dr. Eleanor Vance just arrived from Geneva to review our Kubernetes cluster."),
                ("Context_2", "en-US", "Brian", 1.00, "Did Dr. Vance already inspect the PostgreSQL replication logs on the primary node?"),
                ("Context_3", "en-US", "Emma", 1.00, "She found that Cloudflare blocked two webhook requests during the Geneva migration."),
                ("Context_4", "en-US", "Brian", 1.00, "Let's ask Dr. Vance whether CTranslate2 quantization reduced latency on the CPU worker."),
                ("Context_5", "en-US", "Ava", 1.00, "Both PostgreSQL and Kubernetes metrics returned to normal right after the patch."),
                ("Context_6", "en-US", "Andrew", 1.00, "Great, I will send Dr. Eleanor Vance the final Geneva deployment report tonight.")
            };

            var captured = await CaptureEdgeNeuralSpeechUtterancesAsync(edgeExe, tempDir, script, _output);
            captured.Count.Should().Be(script.Count);

            // Mix realistic musical pad + pink noise (14 dB SNR) into two clips to test BGM + noise resilience
            var dataset = new List<(string Tag, string Speaker, string RefText, byte[] PcmData)>(captured.Count);
            for (int i = 0; i < captured.Count; i++)
            {
                var c = captured[i];
                byte[] pcm = (c.Tag == "Noisy_BGM" || c.Tag == "Long_Clause")
                    ? AddBackgroundMusicAndNoise(c.PcmData, snrDb: 14.0, seed: 100 + i)
                    : c.PcmData;
                dataset.Add((c.Tag, c.Speaker, c.RefText, pcm));
            }

            // Part A: Compare SourceLanguage = Auto vs SourceLanguage = English through full Whisper + EnViT5 pipeline (0% cache hits)
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);

            await using var speechSvc = new WhisperSpeechRecognitionService(settingsSvc);
            await speechSvc.InitializeAsync();
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await transEngine.InitializeAsync();
            await transEngine.TranslateTextAsync("Warmup.", "en", "vi");

            async Task EvalPipelineMode(string modeLabel, string langArg)
            {
                await speechSvc.RecognizeAsync(dataset[0].PcmData, langArg); // warm
                var proc = Process.GetCurrentProcess();
                proc.Refresh();
                var cpuStart = proc.TotalProcessorTime;

                var whisperMsList = new List<long>();
                var transMsList = new List<long>();
                var totalMsList = new List<long>();
                var wers = new List<double>();
                var cers = new List<double>();
                int missing = 0;
                int duplicates = 0;
                string prevNorm = string.Empty;

                foreach (var item in dataset)
                {
                    var sr = await speechSvc.RecognizeAsync(item.PcmData, langArg);
                    if (!sr.HasText || string.IsNullOrWhiteSpace(sr.Text))
                    {
                        missing++;
                        continue;
                    }
                    string curNorm = NormEn(sr.Text);
                    if (curNorm == prevNorm) duplicates++;
                    prevNorm = curNorm;

                    var trSw = Stopwatch.StartNew();
                    string vi = await transEngine.TranslateTextAsync(sr.Text, "en", "vi");
                    trSw.Stop();

                    var (wer, cer) = ScoreEn(item.RefText, sr.Text);
                    wers.Add(wer);
                    cers.Add(cer);
                    whisperMsList.Add(sr.LatencyMs);
                    transMsList.Add(trSw.ElapsedMilliseconds);
                    totalMsList.Add(sr.LatencyMs + trSw.ElapsedMilliseconds);

                    _output.WriteLine($"[EN_REAL_{modeLabel}] Tag={item.Tag} ({item.Speaker}) | WER={wer:P1} CER={cer:P1} | Whisper={sr.LatencyMs}ms Trans={trSw.ElapsedMilliseconds}ms | Hyp=\"{sr.Text}\" -> VI=\"{vi}\"");
                }

                proc.Refresh();
                double cpuSec = (proc.TotalProcessorTime - cpuStart).TotalSeconds;
                var sW = whisperMsList.OrderBy(x => x).ToList();
                var sT = transMsList.OrderBy(x => x).ToList();
                var sTot = totalMsList.OrderBy(x => x).ToList();

                _output.WriteLine(
                    $"[EN_REAL_SUMMARY_{modeLabel}] Count={dataset.Count}, Missing={missing}, Duplicates={duplicates} | " +
                    $"AvgWER={wers.Average():P2}, AvgCER={cers.Average():P2} | " +
                    $"WhisperP50={Pct(sW, 0.5)}ms P95={Pct(sW, 0.95)}ms | " +
                    $"TransP50={Pct(sT, 0.5)}ms P95={Pct(sT, 0.95)}ms | " +
                    $"TotalAfterVadP50={Pct(sTot, 0.5)}ms P95={Pct(sTot, 0.95)}ms | " +
                    $"CpuSec={cpuSec:F2}s, WS={proc.WorkingSet64 / (1024 * 1024)}MB, Priv={proc.PrivateMemorySize64 / (1024 * 1024)}MB");

                missing.Should().Be(0);
                duplicates.Should().Be(0);
                wers.Average().Should().BeLessThan(0.15);
            }

            await EvalPipelineMode("SOURCE_AUTO", "auto");
            await EvalPipelineMode("SOURCE_ENGLISH", "en");

            // Part B: Dedicated A/B Evaluation of WithNoContext() vs Carry-Context (without WithNoContext())
            // on the 6-turn connected proper-noun dialogue + BGM clips
            string modelPath = WhisperModelManager.GetModelPath("base");
            using var factory = WhisperFactory.FromPath(modelPath);

            async Task EvalContextMode(string label, bool useNoContext)
            {
                var builder = factory.CreateBuilder()
                    .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                    .WithGreedySamplingStrategy(g => g.WithBestOf(1))
                    .WithAudioContextSize(768)
                    .WithLanguage("en");
                if (useNoContext) builder.WithNoContext();

                using var processor = builder.Build();
                var ctxWers = new List<double>();
                var ctxCers = new List<double>();
                var allWers = new List<double>();
                var lat = new List<long>();
                int properNounHits = 0;
                string[] targetProperNouns = ["eleanor", "vance", "geneva", "kubernetes", "postgresql", "cloudflare", "ctranslate2"];
                int totalProperNounChecks = 0;

                foreach (var item in dataset)
                {
                    using var wav = WrapPcmAsWav(item.PcmData);
                    var sw = Stopwatch.StartNew();
                    var sb = new StringBuilder();
                    await foreach (var seg in processor.ProcessAsync(wav))
                    {
                        if (!string.IsNullOrWhiteSpace(seg.Text)) sb.Append(seg.Text.Trim()).Append(' ');
                    }
                    sw.Stop();
                    string hyp = sb.ToString().Trim();
                    var (wer, cer) = ScoreEn(item.RefText, hyp);
                    allWers.Add(wer);
                    lat.Add(sw.ElapsedMilliseconds);
                    if (item.Tag.StartsWith("Context_", StringComparison.Ordinal))
                    {
                        ctxWers.Add(wer);
                        ctxCers.Add(cer);
                        string refLower = NormEn(item.RefText);
                        string hypLower = NormEn(hyp);
                        foreach (string pn in targetProperNouns)
                        {
                            if (refLower.Contains(pn, StringComparison.Ordinal))
                            {
                                totalProperNounChecks++;
                                if (hypLower.Contains(pn, StringComparison.Ordinal)) properNounHits++;
                            }
                        }
                    }
                }

                var sLat = lat.OrderBy(x => x).ToList();
                _output.WriteLine(
                    $"[CONTEXT_AB_{label}] All12_WER={allWers.Average():P2} | Context6_WER={ctxWers.Average():P2}, Context6_CER={ctxCers.Average():P2} | " +
                    $"ProperNounRecall={properNounHits}/{totalProperNounChecks} ({(double)properNounHits / Math.Max(1, totalProperNounChecks):P1}) | " +
                    $"P50={Pct(sLat, 0.5)}ms P95={Pct(sLat, 0.95)}ms");
            }

            await EvalContextMode("WITH_NO_CONTEXT", useNoContext: true);
            await EvalContextMode("CARRY_CONTEXT_DEFAULT", useNoContext: false);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Phase3_RealChineseAudioAndHybridValidation_SimplifiedAndTraditional_AutoVsChinese_M2M100AndHybridDedup()
    {
        string edgeExe = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        if (!File.Exists(edgeExe) || !WhisperModelManager.IsModelAvailable("base") || !LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk())
        {
            return;
        }

        using (var devCheck = new WasapiAudioCaptureService())
        {
            if (devCheck.GetOutputDevices().Count == 0) return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"rw_zh_qa_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 8 real Mandarin utterances spoken by 4 Edge Neural Voices:
            // 4 Simplified (zh-CN: 晓晓, 云希) + 4 Traditional (zh-TW: 曉臻, 雲哲)
            var zhScript = new List<(string Tag, string LangPrefix, string VoiceKeyword, double Rate, string Text)>
            {
                ("ZHS_1", "zh-CN", "晓晓", 1.00, "你好，欢迎大家参加今天的软件测试会议。"),
                ("ZHS_2", "zh-CN", "云希", 1.00, "请问明天的国际航班是早上八点起飞吗？"),
                ("ZHS_3", "zh-CN", "晓晓", 1.00, "如果系统出现网络错误，请立即联系技术支持团队。"),
                ("ZHS_4", "zh-CN", "云希", 1.00, "我们已经在本地服务器上完成了数据库备份。"),
                ("ZHT_1", "zh-TW", "曉臻", 1.00, "請問台北車站附近有什麼推薦的餐廳嗎？"),
                ("ZHT_2", "zh-TW", "雲哲", 1.00, "今天下午的天氣非常晴朗，適合出去散步。"),
                ("ZHT_3", "zh-TW", "曉臻", 1.00, "請在離開辦公室之前關閉所有的電腦電源。"),
                ("ZHT_4", "zh-TW", "雲哲", 1.00, "非常感謝各位工程師這一個月以來的辛勤工作。")
            };

            var capturedZh = await CaptureEdgeNeuralSpeechUtterancesAsync(edgeExe, tempDir, zhScript, _output);
            capturedZh.Count.Should().Be(zhScript.Count);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);

            await using var speechSvc = new WhisperSpeechRecognitionService(settingsSvc);
            await speechSvc.InitializeAsync();
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await transEngine.InitializeAsync();
            await transEngine.TranslateTextAsync("你好。", "zh", "vi"); // warm M2M-100

            async Task EvalZhMode(string label, string langArg)
            {
                await speechSvc.RecognizeAsync(capturedZh[0].PcmData, langArg); // warm
                var rawCers = new List<double>();
                var simpCers = new List<double>();
                var zhsSimpCers = new List<double>();
                var zhtSimpCers = new List<double>();
                var whisperLat = new List<long>();
                var m2mLat = new List<long>();
                int langDetectedZh = 0;

                foreach (var item in capturedZh)
                {
                    var sr = await speechSvc.RecognizeAsync(item.PcmData, langArg);
                    if (sr.DetectedLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                        langDetectedZh++;

                    var (rawCer, simpCer) = ScoreZh(item.RefText, sr.Text);
                    rawCers.Add(rawCer);
                    simpCers.Add(simpCer);
                    if (item.Tag.StartsWith("ZHS", StringComparison.Ordinal)) zhsSimpCers.Add(simpCer);
                    else zhtSimpCers.Add(simpCer);

                    var trSw = Stopwatch.StartNew();
                    string vi = await transEngine.TranslateTextAsync(sr.Text, "zh", "vi");
                    trSw.Stop();

                    whisperLat.Add(sr.LatencyMs);
                    m2mLat.Add(trSw.ElapsedMilliseconds);

                    _output.WriteLine($"[ZH_REAL_{label}] {item.Tag} ({item.Speaker}) | DetLang={sr.DetectedLanguage} | RawCER={rawCer:P1} SimpNormCER={simpCer:P1} | Whisper={sr.LatencyMs}ms M2M100={trSw.ElapsedMilliseconds}ms | Ref=\"{item.RefText}\" | Hyp=\"{sr.Text}\" | VI=\"{vi}\"");
                    vi.Should().NotBeNullOrWhiteSpace();
                }

                var sW = whisperLat.OrderBy(x => x).ToList();
                var sM = m2mLat.OrderBy(x => x).ToList();
                _output.WriteLine(
                    $"[ZH_SUMMARY_{label}] Count={capturedZh.Count}, LangAcc={(double)langDetectedZh / capturedZh.Count:P1} | " +
                    $"RawCER={rawCers.Average():P2}, SimpNormCER_All={simpCers.Average():P2} (ZHS={zhsSimpCers.Average():P2}, ZHT={zhtSimpCers.Average():P2}) | " +
                    $"WhisperP50={Pct(sW, 0.5)}ms P95={Pct(sW, 0.95)}ms | M2M100_P50={Pct(sM, 0.5)}ms P95={Pct(sM, 0.95)}ms");

                langDetectedZh.Should().Be(capturedZh.Count);
                simpCers.Average().Should().BeLessThan(0.20, "Whisper Base on real Mandarin neural speech should achieve < 20% script-normalized CER");
            }

            await EvalZhMode("SOURCE_AUTO", "auto");
            await EvalZhMode("SOURCE_CHINESE", "zh");

            // Part B: Verify Chinese Hybrid Mode (OCR + Audio deduplication across Simplified/Traditional + fallback)
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

            Process? edgeHybridProc = null;
            try
            {
                await overlayReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                const string zhOnScreen = "你好，欢迎大家参加今天的软件测试会议。";
                string hybridHtml = Path.Combine(tempDir, "zh_hybrid.html");
                string hybridProf = Path.Combine(tempDir, "zh_hybrid_prof");
                Directory.CreateDirectory(hybridProf);

                await File.WriteAllTextAsync(hybridHtml, $$"""
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>ScreenSub ZH Hybrid QA</title>
<style>
  html, body { margin: 0; padding: 0; width: 100%; height: 100%; background: #0b0f17; display: flex; align-items: flex-end; justify-content: center; font-family: 'Microsoft YaHei', sans-serif; }
  #sub { width: 92%; margin-bottom: 18px; padding: 14px 20px; background: rgba(0,0,0,0.9); color: #ffffff; font-size: 32px; font-weight: 700; text-align: center; border-radius: 8px; }
</style></head>
<body><div id="sub">{{zhOnScreen}}</div></body></html>
""", Encoding.UTF8);

                edgeHybridProc = Process.Start(new ProcessStartInfo
                {
                    FileName = edgeExe,
                    Arguments = $"--new-window --window-size=960,260 --window-position=80,60 --app=\"{new Uri(hybridHtml).AbsoluteUri}\" --user-data-dir=\"{hybridProf}\" --no-first-run --no-default-browser-check",
                    UseShellExecute = false
                });

                nint hwnd = nint.Zero;
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(150);
                    foreach (var p in Process.GetProcessesByName("msedge"))
                    {
                        if (p.MainWindowHandle != nint.Zero && p.MainWindowTitle.Contains("ScreenSub ZH Hybrid QA", StringComparison.OrdinalIgnoreCase))
                        {
                            hwnd = p.MainWindowHandle;
                            break;
                        }
                    }
                    if (hwnd != nint.Zero) break;
                }
                hwnd.Should().NotBe(nint.Zero);

                GetWindowRect(hwnd, out var rect);
                var captureRegion = new ScreenRegion(rect.Left + 20, Math.Max(rect.Top + 40, rect.Bottom - 150), Math.Max(400, rect.Right - rect.Left - 40), 130);

                settingsSvc.Settings.CaptureFps = 4;
                settingsSvc.Settings.CaptureSource = CaptureSourceType.Window;
                settingsSvc.Settings.RegionMode = RegionMode.FollowWindow;
                settingsSvc.Settings.TranslationMode = TranslationMode.Hybrid;
                settingsSvc.Settings.SourceLanguage = SourceLanguage.Chinese;
                settingsSvc.Settings.SpeechLanguage = "zh";
                settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];

                string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
                await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
                await ocrEngine.InitializeAsync();

                var cache = new TranslationCache();
                using var localProv = new LocalTranslationProvider(transEngine, cache, settingsSvc);
                var resolver = new TranslationProviderResolver(settingsSvc, localProv, new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache), new MockTranslationProvider());
                var capSvc = new ScreenCaptureService();
                var winInfo = capSvc.GetWindowInfo(hwnd);
                var vad = new EnergyVoiceActivityDetector();
                using var dummyAudio = new WasapiAudioCaptureService();

                await using var coord = new SubtitleCoordinator(capSvc, ocrEngine, resolver, settingsSvc, new FrameChangeDetector(), dummyAudio, vad, speechSvc);
                coord.UpdateSelectedWindow(winInfo);
                coord.UpdateSelectedRegion(captureRegion);
                coord.SetTranslationMode(TranslationMode.Hybrid);

                var ocrReadyTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource<SpeechRecognitionResult>? speechDoneTcs = null;
                TaskCompletionSource<SubtitleItem>? distinctAudioSubTcs = null;
                int audioOverwritesDuringMatch = 0;
                bool checkMatchDup = false;

                coord.SpeechRecognized += (_, sr) => speechDoneTcs?.TrySetResult(sr);
                coord.SubtitleUpdated += (_, sub) =>
                {
                    if (!sub.HasContent) return;
                    uiDispatcher?.Invoke(() => overlay!.UpdateSubtitle(sub));
                    if (sub.SourceMode == TranslationMode.ScreenOcr)
                        ocrReadyTcs.TrySetResult(sub);
                    else if (sub.SourceMode == TranslationMode.AudioRealtime)
                    {
                        if (checkMatchDup) Interlocked.Increment(ref audioOverwritesDuringMatch);
                        distinctAudioSubTcs?.TrySetResult(sub);
                    }
                };

                coord.Start();
                var zhOcrSub = await ocrReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

                // Feed matching Chinese audio (ZHS_1) into VAD -> must deduplicate against Chinese OCR text
                speechDoneTcs = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                checkMatchDup = true;
                long dupBefore = coord.DuplicateSuppressedCount;
                vad.ProcessAudioChunk(capturedZh[0].PcmData, 16000);
                vad.ProcessAudioChunk(new byte[16000 * 2], 16000); // 1s silence to trigger HangoverMs
                var dupSpeech = await speechDoneTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Task.Delay(800);
                checkMatchDup = false;

                _output.WriteLine($"[ZH_HYBRID_DEDUP] OcrText=\"{zhOcrSub.OriginalText}\" -> VI=\"{zhOcrSub.VietnameseText}\" | SpeechText=\"{dupSpeech.Text}\" | Suppressed={audioOverwritesDuringMatch == 0} (DupCountDelta={coord.DuplicateSuppressedCount - dupBefore})");
                audioOverwritesDuringMatch.Should().Be(0, "Chinese Hybrid mode must deduplicate matching Chinese speech against Chinese OCR");

                // Feed distinct Chinese audio (ZHT_3: 請在離開辦公室之前關閉所有的電腦電源。) -> must translate & publish
                speechDoneTcs = new TaskCompletionSource<SpeechRecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                distinctAudioSubTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);
                vad.ProcessAudioChunk(capturedZh[6].PcmData, 16000);
                vad.ProcessAudioChunk(new byte[16000 * 2], 16000);

                var distSpeech = await speechDoneTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var distSub = await distinctAudioSubTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                _output.WriteLine($"[ZH_HYBRID_DISTINCT] SpeechText=\"{distSpeech.Text}\" -> VI=\"{distSub.VietnameseText}\"");
                distSub.VietnameseText.Should().NotBeNullOrWhiteSpace();

                coord.Stop();
            }
            finally
            {
                uiDispatcher?.InvokeShutdown();
                staThread.Join(2000);
                try { edgeHybridProc?.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Phase4_RealtimeConcurrency_SimultaneousOcrWhisperAndUncachedTranslation_CpuContentionAudit()
    {
        string edgeExe = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        if (!File.Exists(edgeExe) || !WhisperModelManager.IsModelAvailable("base") || !LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk())
        {
            return;
        }

        using (var devCheck = new WasapiAudioCaptureService())
        {
            if (devCheck.GetOutputDevices().Count == 0) return;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), $"rw_concurrency_{Guid.NewGuid():N}");
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

        Process? edgeProc = null;
        try
        {
            await overlayReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // 6 unique spoken sentences (never cached)
            string[] spokenSentences =
            [
                "The satellite telemetry stream reported nominal orbital velocity this morning.",
                "Engineers replaced the faulty optical transceiver inside the primary rack.",
                "Heavy snowfall across the mountain pass delayed the freight locomotive.",
                "Pleaseupload the encrypted diagnostic bundle before closing the ticket.",
                "Our quarterly security audit verified every firewall rule in production.",
                "The marine biologist recorded humpback whale songs near the coral reef."
            ];

            // Pre-synthesize WAVs for deterministic simultaneous playback through real speakers -> WASAPI loopback
            var wavPaths = new List<string>(spokenSentences.Length);
            for (int i = 0; i < spokenSentences.Length; i++)
            {
                string wp = Path.Combine(tempDir, $"conc_spk_{i}.wav");
                string escText = spokenSentences[i].Replace("Pleaseupload", "Please upload").Replace("'", "''");
                string escPath = wp.Replace("'", "''");
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"Add-Type -AssemblyName System.Speech; $s=New-Object System.Speech.Synthesis.SpeechSynthesizer; $s.Volume=80; $s.Rate=-1; $s.SetOutputToWaveFile('{escPath}'); $s.Speak('{escText}'); $s.Dispose()\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!;
                await p.WaitForExitAsync();
                wavPaths.Add(wp);
            }

            // 6 unique on-screen visual cues rotating every 2.2s in Edge window concurrently with spoken audio
            string htmlPath = Path.Combine(tempDir, "concurrent_player.html");
            string profDir = Path.Combine(tempDir, "conc_edge_prof");
            Directory.CreateDirectory(profDir);

            await File.WriteAllTextAsync(htmlPath, """
<!DOCTYPE html>
<html><head><meta charset="utf-8"><title>ScreenSub Concurrency QA</title></head>
<body style="margin:0;background:#0f172a;color:#f8fafc;font-family:'Segoe UI',sans-serif;overflow:hidden;">
  <div style="width:100vw;height:100vh;display:flex;flex-direction:column;justify-content:flex-end;align-items:center;padding-bottom:26px;box-sizing:border-box;">
    <div id="sub" style="background:rgba(0,0,0,0.82);padding:10px 26px;border-radius:6px;font-size:28px;font-weight:700;text-align:center;">
      Quantum cryptography protects global networks.
    </div>
  </div>
<script>
const cues = [
  "Quantum cryptography protects global networks.",
  "Autonomous drones mapped the deep volcanic crater yesterday!",
  "Solar panels generated surplus power.",
  "The symphony orchestra rehearsed the final movement in the grand hall.",
  "Ancient mineral deposits were found!",
  "High-speed rail connects the coastal harbor with the mountain valley.",
  "Deep space telescopes captured infrared light from distant galaxies.",
  "Biomedical sensors monitor blood oxygen levels continuously!",
  "Submarine cables carry terabits of internet traffic across the ocean.",
  "Wind turbines offshore supplied clean energy to the northern grid!",
  "Robotic arms assembled precision microprocessors inside the cleanroom.",
  "Meteorologists tracked the tropical cyclone across the Pacific basin.",
  "Archival manuscripts were digitized using multispectral imaging cameras!",
  "Geothermal power plants operate reliably throughout the winter season.",
  "Acoustic metamaterials absorb low-frequency vibration inside aircraft cabins.",
  " ceramic superconductors cooled with liquid nitrogen reached zero resistance!"
];
let idx = 0;
setInterval(() => {
  if (idx + 1 < cues.length) {
    idx++;
    document.getElementById('sub').textContent = cues[idx];
  }
}, 3200);
</script></body></html>
""", Encoding.UTF8);

            edgeProc = Process.Start(new ProcessStartInfo
            {
                FileName = edgeExe,
                Arguments = $"--new-window --window-size=960,260 --window-position=80,60 --app=\"{new Uri(htmlPath).AbsoluteUri}\" --user-data-dir=\"{profDir}\" --no-first-run --no-default-browser-check",
                UseShellExecute = false
            });

            nint hwnd = nint.Zero;
            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(150);
                foreach (var p in Process.GetProcessesByName("msedge"))
                {
                    if (p.MainWindowHandle != nint.Zero && p.MainWindowTitle.Contains("ScreenSub Concurrency QA", StringComparison.OrdinalIgnoreCase))
                    {
                        hwnd = p.MainWindowHandle;
                        break;
                    }
                }
                if (hwnd != nint.Zero) break;
            }
            hwnd.Should().NotBe(nint.Zero);

            GetWindowRect(hwnd, out var rect);
            var captureRegion = new ScreenRegion(rect.Left + 20, Math.Max(rect.Top + 40, rect.Bottom - 150), Math.Max(400, rect.Right - rect.Left - 40), 130);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "cfg"));
            await settingsSvc.LoadSettingsAsync();
            settingsSvc.Settings.ApplyBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
            settingsSvc.Settings.CaptureFps = 5;
            settingsSvc.Settings.CaptureSource = CaptureSourceType.Window;
            settingsSvc.Settings.RegionMode = RegionMode.FollowWindow;
            settingsSvc.Settings.TranslationMode = TranslationMode.Hybrid;
            settingsSvc.Settings.SourceLanguage = SourceLanguage.English;
            settingsSvc.Settings.SpeechLanguage = "en";
            settingsSvc.Settings.TargetLanguages = [TargetLanguage.Vietnamese];

            await using var speechSvc = new WhisperSpeechRecognitionService(settingsSvc);
            string ocrWorkerPath = ResolveRepoRelative("workers", "paddle-ocr", "ocr_worker.py");
            string transWorkerPath = ResolveRepoRelative("workers", "translation", "translation_worker.py");
            await using var ocrEngine = new PaddleOcrEngine("python", ocrWorkerPath);
            using var transEngine = new LocalNeuralTranslationEngine(settingsSvc, "python", transWorkerPath);
            await Task.WhenAll(speechSvc.InitializeAsync(), ocrEngine.InitializeAsync(), transEngine.InitializeAsync());
            await transEngine.TranslateTextAsync("Warmup.", "en", "vi");

            var cache = new TranslationCache();
            using var localProv = new LocalTranslationProvider(transEngine, cache, settingsSvc);
            var resolver = new TranslationProviderResolver(settingsSvc, localProv, new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settingsSvc, cache), new MockTranslationProvider());
            using var wasapi = new WasapiAudioCaptureService();
            var vad = new EnergyVoiceActivityDetector();
            var capSvc = new ScreenCaptureService();
            var winInfo = capSvc.GetWindowInfo(hwnd);

            await using var coord = new SubtitleCoordinator(capSvc, ocrEngine, resolver, settingsSvc, new FrameChangeDetector(), wasapi, vad, speechSvc);
            coord.UpdateSelectedWindow(winInfo);
            coord.UpdateSelectedRegion(captureRegion);
            coord.SetTranslationMode(TranslationMode.Hybrid);

            var ocrLatencies = new ConcurrentBag<long>();
            var ocrTransLatencies = new ConcurrentBag<long>();
            var whisperLatencies = new ConcurrentBag<long>();
            var audioTransLatencies = new ConcurrentBag<long>();
            var speechQueueWaitLatencies = new ConcurrentBag<long>();
            var speechOnsetToOverlayLatencies = new ConcurrentBag<long>();
            var speechEndToOverlayLatencies = new ConcurrentBag<long>();

            long firstVoiceTick = 0;
            long lastVoiceTick = 0;
            TaskCompletionSource<SubtitleItem>? audioSubTcs = null;

            wasapi.AudioDataAvailable += (_, e) =>
            {
                double sum = 0;
                int n = e.Pcm16Data.Length / 2;
                for (int i = 0; i < n; i++)
                {
                    short s = (short)(e.Pcm16Data[2 * i] | (e.Pcm16Data[2 * i + 1] << 8));
                    double f = s / 32768.0;
                    sum += f * f;
                }
                if (n > 0 && Math.Sqrt(sum / n) >= vad.SilenceThreshold)
                {
                    long t = Stopwatch.GetTimestamp();
                    Interlocked.CompareExchange(ref firstVoiceTick, t, 0);
                    Interlocked.Exchange(ref lastVoiceTick, t);
                }
            };

            coord.SpeechRecognized += (_, sr) =>
            {
                if (sr.HasText)
                {
                    whisperLatencies.Add(sr.LatencyMs);
                    speechQueueWaitLatencies.Add(coord.LastSpeechQueueWaitMs);
                }
            };

            coord.SubtitleUpdated += (_, sub) =>
            {
                if (!sub.HasContent) return;
                uiDispatcher?.Invoke(() => overlay!.UpdateSubtitle(sub));
                long nowTick = Stopwatch.GetTimestamp();

                if (sub.SourceMode == TranslationMode.ScreenOcr)
                {
                    if (coord.LastOcrLatencyMs > 0) ocrLatencies.Add(coord.LastOcrLatencyMs);
                    if (coord.LastTranslationLatencyMs > 0) ocrTransLatencies.Add(coord.LastTranslationLatencyMs);
                }
                else if (sub.SourceMode == TranslationMode.AudioRealtime)
                {
                    if (coord.LastTranslationLatencyMs > 0) audioTransLatencies.Add(coord.LastTranslationLatencyMs);
                    long t0 = Interlocked.Read(ref firstVoiceTick);
                    long tEnd = Interlocked.Read(ref lastVoiceTick);
                    if (t0 > 0 && nowTick > t0)
                        speechOnsetToOverlayLatencies.Add((long)Math.Round((nowTick - t0) * 1000.0 / Stopwatch.Frequency));
                    if (tEnd > 0 && nowTick > tEnd)
                        speechEndToOverlayLatencies.Add((long)Math.Round((nowTick - tEnd) * 1000.0 / Stopwatch.Frequency));
                    audioSubTcs?.TrySetResult(sub);
                }
            };

            var hostProc = Process.GetCurrentProcess();
            var pyProcsBefore = Process.GetProcessesByName("python").ToDictionary(p => p.Id, p =>
            {
                try { return p.TotalProcessorTime; } catch { return TimeSpan.Zero; }
            });
            hostProc.Refresh();
            var hostCpuBefore = hostProc.TotalProcessorTime;
            var wallSw = Stopwatch.StartNew();

            coord.Start();
            await Task.Delay(500);

            for (int i = 0; i < wavPaths.Count; i++)
            {
                Interlocked.Exchange(ref firstVoiceTick, 0);
                Interlocked.Exchange(ref lastVoiceTick, 0);
                audioSubTcs = new TaskCompletionSource<SubtitleItem>(TaskCreationOptions.RunContinuationsAsynchronously);

                string esc = wavPaths[i].Replace("'", "''");
                using (var sp = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"$p=New-Object System.Media.SoundPlayer '{esc}'; $p.PlaySync(); $p.Dispose()\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                })!)
                {
                    await sp.WaitForExitAsync();
                }

                var pubSub = await audioSubTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                _output.WriteLine($"[CONC_AUDIO_{i + 1}] Orig=\"{pubSub.OriginalText}\" -> VI=\"{pubSub.VietnameseText}\" | WhisperMs={coord.LastSpeechLatencyMs}ms, TransMs={coord.LastTranslationLatencyMs}ms, SpeechQueueWaitMs={coord.LastSpeechQueueWaitMs}ms");
                await Task.Delay(600);
            }

            wallSw.Stop();
            coord.Stop();

            hostProc.Refresh();
            double hostCpuSec = (hostProc.TotalProcessorTime - hostCpuBefore).TotalSeconds;
            double pyCpuSec = 0;
            long pyWsMb = 0;
            foreach (var p in Process.GetProcessesByName("python"))
            {
                try
                {
                    if (pyProcsBefore.TryGetValue(p.Id, out var prev))
                    {
                        pyCpuSec += (p.TotalProcessorTime - prev).TotalSeconds;
                        pyWsMb += p.WorkingSet64 / (1024 * 1024);
                    }
                }
                catch { }
            }

            double wallSec = wallSw.Elapsed.TotalSeconds;
            double totalCpuPct = (hostCpuSec + pyCpuSec) / (wallSec * Environment.ProcessorCount) * 100.0;

            var sOcr = ocrLatencies.OrderBy(x => x).ToList();
            var sWhisp = whisperLatencies.OrderBy(x => x).ToList();
            var sTrans = ocrTransLatencies.Concat(audioTransLatencies).OrderBy(x => x).ToList();
            var sQWait = speechQueueWaitLatencies.OrderBy(x => x).ToList();
            var sOnset = speechOnsetToOverlayLatencies.OrderBy(x => x).ToList();
            var sEnd = speechEndToOverlayLatencies.OrderBy(x => x).ToList();

            _output.WriteLine(
                $"[CONCURRENCY_SUMMARY] Wall={wallSec:F1}s | CacheHitRate={coord.CacheHitRate:P1} | " +
                $"OcrCount={sOcr.Count}, OcrP50={Pct(sOcr, 0.5)}ms P95={Pct(sOcr, 0.95)}ms | " +
                $"WhisperCount={sWhisp.Count}, WhisperP50={Pct(sWhisp, 0.5)}ms P95={Pct(sWhisp, 0.95)}ms | " +
                $"UncachedTransCount={sTrans.Count}, TransP50={Pct(sTrans, 0.5)}ms P95={Pct(sTrans, 0.95)}ms | " +
                $"SpeechQueueWaitP50={Pct(sQWait, 0.5)}ms P95={Pct(sQWait, 0.95)}ms | " +
                $"SpeechEndToOvP50={Pct(sEnd, 0.5)}ms P95={Pct(sEnd, 0.95)}ms | " +
                $"SpeechOnsetToOvP50={Pct(sOnset, 0.5)}ms P95={Pct(sOnset, 0.95)}ms | " +
                $"DroppedObsolete={coord.DroppedObsoleteCount}, RejectedStale={coord.RejectedStaleCount}, DupSuppressed={coord.DuplicateSuppressedCount} | " +
                $"HostCpu={hostCpuSec:F2}s, PyCpu={pyCpuSec:F2}s, TotalNormalizedCpu={totalCpuPct:F1}%, HostWS={hostProc.WorkingSet64 / (1024 * 1024)}MB, PyWS={pyWsMb}MB");

            sOcr.Count.Should().BeGreaterThanOrEqualTo(3);
            sWhisp.Count.Should().Be(6);
            coord.CacheHitRate.Should().Be(0.0, "all concurrent OCR and Audio sentences were unique to prevent cache hit bias");
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
                    if (p.MainWindowTitle.Contains("ScreenSub Concurrency QA", StringComparison.OrdinalIgnoreCase))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
            }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }
}
