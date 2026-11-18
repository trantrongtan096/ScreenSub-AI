using System.Diagnostics;
using System.IO;
using System.Text;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace ScreenSub.Speech.Whisper;

public sealed class WhisperSpeechRecognitionService : ISpeechRecognitionService
{
    private WhisperFactory? _factory;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ISettingsService _settingsService;
    private string _loadedModelPath = string.Empty;
    private bool _isDisposed;

    public bool IsModelLoaded => _factory != null;
    public string CurrentModelName { get; private set; } = "whisper-base";
    public ServiceStatus Status { get; private set; } = ServiceStatus.Stopped;
    public string StatusMessage { get; private set; } = "Chưa khởi động";

    public WhisperSpeechRecognitionService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (_factory != null) return;

            Status = ServiceStatus.Starting;
            StatusMessage = "Đang nạp mô hình Whisper...";

            string modelType = _settingsService.Settings.SpeechPerformanceMode switch
            {
                SpeechPerformanceMode.Fast => "tiny",
                SpeechPerformanceMode.Quality => "small",
                _ => "base"
            };

            CurrentModelName = $"whisper-{modelType}";
            string modelPath = WhisperModelManager.GetModelPath(
                modelType,
                _settingsService.Settings.ModelStorageDirectory,
                _settingsService.Settings.WhisperModelPath);

            if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < 1024 * 1024)
            {
                Status = ServiceStatus.Degraded;
                StatusMessage = $"Chưa có mô hình {CurrentModelName}. Nhấn 'Tải mô hình' trong Cài đặt.";
                return;
            }

            _factory = WhisperFactory.FromPath(modelPath);
            _loadedModelPath = modelPath;

            Status = ServiceStatus.Running;
            StatusMessage = $"Sẵn sàng ({CurrentModelName})";
        }
        catch (Exception ex)
        {
            Status = ServiceStatus.Error;
            StatusMessage = $"Lỗi khởi động Whisper: {ex.Message}";
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<SpeechRecognitionResult> RecognizeAsync(
        byte[] pcm16Mono16kData,
        string language = "auto",
        CancellationToken ct = default)
    {
        if (pcm16Mono16kData == null || pcm16Mono16kData.Length == 0)
        {
            return SpeechRecognitionResult.Empty;
        }

        if (_factory == null)
        {
            await InitializeAsync(ct);
            if (_factory == null)
            {
                return SpeechRecognitionResult.Empty;
            }
        }

        if (!await _semaphore.WaitAsync(5000, ct))
        {
            return SpeechRecognitionResult.Empty;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            string mappedLang = MapLanguageCode(language);

            using var wavStream = CreateWavStream(pcm16Mono16kData, 16000, 1, 16);

            var builder = _factory.CreateBuilder()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                .WithGreedySamplingStrategy(g => g.WithBestOf(1))
                .WithNoContext();

            // VAD caps normal segments at ~6s; restricting encoder context to 768 tokens (~15.36s)
            // eliminates ~50% redundant encoder self-attention over zero-padded frames with equal accuracy.
            double audioDurationSec = pcm16Mono16kData.Length / 32000.0;
            if (audioDurationSec <= 8.0)
            {
                builder.WithAudioContextSize(768);
            }

            if (!string.IsNullOrEmpty(mappedLang) && !mappedLang.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                builder.WithLanguage(mappedLang);
            }
            else
            {
                builder.WithLanguage("auto");
            }

            using var processor = builder.Build();

            var sb = new StringBuilder();
            string detectedLanguage = mappedLang;
            double minProb = 1.0;

            await foreach (var segmentData in processor.ProcessAsync(wavStream, ct))
            {
                if (!string.IsNullOrWhiteSpace(segmentData.Text))
                {
                    sb.Append(segmentData.Text.Trim()).Append(' ');
                }

                if (segmentData.MinProbability < minProb)
                {
                    minProb = segmentData.MinProbability;
                }

                if (!string.IsNullOrEmpty(segmentData.Language))
                {
                    detectedLanguage = segmentData.Language;
                }
            }

            sw.Stop();
            string recognizedText = sb.ToString().Trim();

            return new SpeechRecognitionResult
            {
                SegmentId = 0,
                Text = recognizedText,
                DetectedLanguage = NormalizeLanguageCode(detectedLanguage),
                IsFinal = true,
                Confidence = minProb,
                LatencyMs = sw.ElapsedMilliseconds,
                Timestamp = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            return SpeechRecognitionResult.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi nhận dạng: {ex.Message}";
            return SpeechRecognitionResult.Empty;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static MemoryStream CreateWavStream(byte[] pcmData, int sampleRate, short channels, short bitsPerSample)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int subChunk2Size = pcmData.Length;
        int chunkSize = 36 + subChunk2Size;

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(chunkSize);
        writer.Write("WAVE"u8);

        // fmt chunk
        writer.Write("fmt "u8);
        writer.Write(16); // SubChunk1Size (16 for PCM)
        writer.Write((short)1); // AudioFormat (1 for PCM)
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // data chunk
        writer.Write("data"u8);
        writer.Write(subChunk2Size);
        writer.Write(pcmData);

        stream.Position = 0;
        return stream;
    }

    private static string MapLanguageCode(string lang) => lang.ToLowerInvariant() switch
    {
        "en" or "english" => "en",
        "zh" or "chinese" => "zh",
        "vi" or "vietnamese" => "vi",
        _ => "auto"
    };

    private static string NormalizeLanguageCode(string code) => code.ToLowerInvariant() switch
    {
        "en" => "en",
        "zh" => "zh",
        "vi" => "vi",
        _ => code
    };

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        await _semaphore.WaitAsync();
        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        catch { }
        finally
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}

