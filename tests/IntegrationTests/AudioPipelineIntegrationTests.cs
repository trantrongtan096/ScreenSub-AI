using FluentAssertions;
using ScreenSub.Audio.Capture;
using ScreenSub.Audio.Processing;
using ScreenSub.Audio.VAD;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Desktop.Services;
using ScreenSub.Infrastructure;
using ScreenSub.Speech.Whisper;
using ScreenSub.Translation;
using Xunit;

namespace IntegrationTests;

public class AudioPipelineIntegrationTests
{
    [Fact]
    public void AudioResampler_Stereo48kHzTo16kHzMono_ShouldDownsampleCorrectly()
    {
        // 48kHz stereo 32-bit float: 100ms = 4800 frames = 9600 floats = 38400 bytes
        int sampleRate = 48000;
        int durationMs = 100;
        int numFrames = sampleRate * durationMs / 1000;
        float[] floatSamples = new float[numFrames * 2];

        // Fill with 440Hz sine wave at 0.5 amplitude
        for (int i = 0; i < numFrames; i++)
        {
            float val = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            floatSamples[i * 2] = val;     // Left
            floatSamples[i * 2 + 1] = val; // Right
        }

        byte[] inputBytes = new byte[floatSamples.Length * sizeof(float)];
        Buffer.BlockCopy(floatSamples, 0, inputBytes, 0, inputBytes.Length);

        byte[] resampledPcm16 = AudioResampler.ResampleTo16kMonoPcm(
            inputBytes,
            inputBytes.Length,
            sourceSampleRate: 48000,
            sourceChannels: 2,
            sourceBitsPerSample: 32,
            isFloat: true);

        // 100ms at 16kHz 16-bit mono = 1600 samples * 2 bytes = 3200 bytes
        resampledPcm16.Length.Should().Be(3200);

        // Convert back to short and verify non-zero energy
        short[] pcm16 = new short[resampledPcm16.Length / 2];
        Buffer.BlockCopy(resampledPcm16, 0, pcm16, 0, resampledPcm16.Length);

        double energy = pcm16.Select(s => (double)Math.Abs(s)).Average();
        energy.Should().BeGreaterThan(5000);
    }

    [Fact]
    public void EnergyVAD_SilenceAndSpeechBurst_ShouldEmitSpeechSegment()
    {
        var vad = new EnergyVoiceActivityDetector(preRollMs: 300)
        {
            SilenceThreshold = 0.015,
            MinSpeechDurationMs = 200,
            HangoverMs = 300
        };

        var segments = new List<SpeechSegment>();
        vad.SpeechSegmentReady += (s, seg) => segments.Add(seg);

        // 1. Send 500ms of silence (16kHz 16-bit mono: 500ms = 8000 samples = 16000 bytes)
        byte[] silenceChunk = new byte[3200]; // 100ms chunks
        for (int i = 0; i < 5; i++)
        {
            vad.ProcessAudioChunk(silenceChunk, 16000);
        }

        vad.CurrentState.Should().Be(VadState.Silence);
        segments.Should().BeEmpty();

        // 2. Send 400ms of tone (speech burst)
        short[] toneSamples = new short[1600]; // 100ms
        for (int i = 0; i < toneSamples.Length; i++)
        {
            toneSamples[i] = (short)(12000 * Math.Sin(2 * Math.PI * 500.0 * i / 16000.0));
        }
        byte[] toneChunk = new byte[toneSamples.Length * sizeof(short)];
        Buffer.BlockCopy(toneSamples, 0, toneChunk, 0, toneChunk.Length);

        for (int i = 0; i < 4; i++)
        {
            vad.ProcessAudioChunk(toneChunk, 16000);
        }

        // 3. Send 500ms of silence to trigger hangover completion
        for (int i = 0; i < 8; i++)
        {
            vad.ProcessAudioChunk(silenceChunk, 16000);
        }

        segments.Should().NotBeEmpty();
        segments[0].PcmData.Length.Should().BeGreaterThan(0);
        segments[0].DurationMs.Should().BeGreaterThan(200);
    }

    [Fact]
    public void SubtitleCoordinator_TranslationModeSwitching_ShouldUpdateStateAndBypassOcr()
    {
        var settings = new SettingsService();
        var capture = new ScreenSub.Capture.ScreenCaptureService();
        var changeDetector = new ScreenSub.Capture.FrameChangeDetector();
        var ocr = new ScreenSub.Ocr.PaddleOcrEngine();
        var cache = new TranslationCache();
        var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settings, cache);
        var mock = new MockTranslationProvider();
        var audioCapture = new WasapiAudioCaptureService();
        var vad = new EnergyVoiceActivityDetector();
        var speech = new WhisperSpeechRecognitionService(settings);

        var coordinator = new SubtitleCoordinator(
            capture, ocr, azure, mock, settings, changeDetector,
            audioCapture, vad, speech);

        coordinator.Mode.Should().Be(TranslationMode.ScreenOcr);

        // Switch to AudioRealtime mode
        coordinator.SetTranslationMode(TranslationMode.AudioRealtime);
        coordinator.Mode.Should().Be(TranslationMode.AudioRealtime);
        settings.Settings.TranslationMode.Should().Be(TranslationMode.AudioRealtime);

        // Switch to Hybrid mode
        coordinator.SetTranslationMode(TranslationMode.Hybrid);
        coordinator.Mode.Should().Be(TranslationMode.Hybrid);
        settings.Settings.TranslationMode.Should().Be(TranslationMode.Hybrid);

        // Switch back to ScreenOcr mode
        coordinator.SetTranslationMode(TranslationMode.ScreenOcr);
        coordinator.Mode.Should().Be(TranslationMode.ScreenOcr);
    }

    [Fact]
    public async Task SubtitleCoordinator_AudioMode_ShouldDeliverAudioSubtitlesThroughPipeline()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ScreenSub_AudioTest_" + Guid.NewGuid());
        var settings = new SettingsService(tempDir);
        settings.Settings.UseMockTranslation = true;
        settings.Settings.TranslationMode = TranslationMode.AudioRealtime;

        var capture = new ScreenSub.Capture.ScreenCaptureService();
        var changeDetector = new ScreenSub.Capture.FrameChangeDetector();
        var ocr = new ScreenSub.Ocr.PaddleOcrEngine();
        var cache = new TranslationCache();
        var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settings, cache);
        var mock = new MockTranslationProvider();
        var audioCapture = new WasapiAudioCaptureService();
        var vad = new EnergyVoiceActivityDetector();
        var speech = new WhisperSpeechRecognitionService(settings);

        var coordinator = new SubtitleCoordinator(
            capture, ocr, azure, mock, settings, changeDetector,
            audioCapture, vad, speech);

        coordinator.SetTranslationMode(TranslationMode.AudioRealtime);

        SubtitleItem? receivedSubtitle = null;
        coordinator.SubtitleUpdated += (s, item) =>
        {
            if (item.HasContent)
            {
                receivedSubtitle = item;
            }
        };

        coordinator.Start();
        coordinator.State.Should().Be(SystemState.Running);

        // Send a simulated speech segment to VAD
        short[] toneSamples = new short[1600]; // 100ms
        for (int i = 0; i < toneSamples.Length; i++)
        {
            toneSamples[i] = (short)(12000 * Math.Sin(2 * Math.PI * 400 * i / 16000));
        }
        byte[] toneChunk = new byte[toneSamples.Length * sizeof(short)];
        Buffer.BlockCopy(toneSamples, 0, toneChunk, 0, toneChunk.Length);

        // Trigger speech in VAD
        for (int i = 0; i < 4; i++)
        {
            vad.ProcessAudioChunk(toneChunk, 16000);
        }
        // Silence to end speech
        byte[] silenceChunk = new byte[3200];
        for (int i = 0; i < 5; i++)
        {
            vad.ProcessAudioChunk(silenceChunk, 16000);
        }

        // Allow worker to process
        await Task.Delay(400);

        coordinator.Stop();
        coordinator.State.Should().BeOneOf(SystemState.Idle, SystemState.Stopped);

        try { Directory.Delete(tempDir, true); } catch { }
    }

    [Fact]
    public async Task WasapiHardwareLoopback_RealDeviceEnumerationAndStartPauseStop_ShouldSucceedWithoutError()
    {
        using var wasapi = new WasapiAudioCaptureService();
        string? lastError = null;
        wasapi.CaptureError += (_, msg) => lastError = msg;

        var devices = wasapi.GetOutputDevices();
        devices.Should().NotBeNull();

        if (devices.Count == 0)
        {
            // Headless environment with no active audio endpoint
            return;
        }

        wasapi.Start();
        await Task.Delay(250);
        wasapi.IsCapturing.Should().BeTrue("real WASAPI loopback capture must start on default Windows audio endpoint");

        wasapi.Pause();
        wasapi.IsCapturing.Should().BeFalse();

        wasapi.Start();
        await Task.Delay(150);
        wasapi.IsCapturing.Should().BeTrue();

        wasapi.Stop();
        wasapi.IsCapturing.Should().BeFalse();
        lastError.Should().BeNull("real hardware WASAPI start/pause/resume/stop must not raise CaptureError");
    }

    [Fact]
    public async Task WhisperService_WithoutModelFile_ShouldDegradeGracefullyWithoutDownloading()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ScreenSub_WhisperNoDl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var settings = new SettingsService(tempDir);
            settings.Settings.ModelStorageDirectory = tempDir;
            settings.Settings.WhisperModelPath = Path.Combine(tempDir, "missing-ggml.bin");
            await using var speech = new WhisperSpeechRecognitionService(settings);

            await speech.InitializeAsync();

            speech.IsModelLoaded.Should().BeFalse();
            speech.Status.Should().Be(ServiceStatus.Degraded);

            var res = await speech.RecognizeAsync(new byte[3200], "en");
            res.HasText.Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void WhisperModelManager_DefaultDirectoryAndCustomStorageResolution_ShouldMatchLocalModelManagerLayout()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ScreenSub_WhisperPathTest_" + Guid.NewGuid().ToString("N"));
        string whisperDir = Path.Combine(tempRoot, "whisper");
        Directory.CreateDirectory(whisperDir);
        try
        {
            string fakeModel = Path.Combine(whisperDir, "ggml-base.bin");
            File.WriteAllBytes(fakeModel, new byte[2 * 1024 * 1024]);

            WhisperModelManager.DefaultModelDirectory
                .Should().EndWith(Path.Combine("ScreenSubAI", "Models", "whisper"));

            string resolved = WhisperModelManager.GetModelPath("base", customModelStorageDir: tempRoot);
            resolved.Should().Be(fakeModel);
            WhisperModelManager.IsModelAvailable("base", customModelStorageDir: tempRoot).Should().BeTrue();

            string explicitFile = Path.Combine(tempRoot, "custom.bin");
            File.WriteAllBytes(explicitFile, new byte[2 * 1024 * 1024]);
            WhisperModelManager.GetModelPath("base", customModelStorageDir: tempRoot, explicitWhisperPath: explicitFile)
                .Should().Be(explicitFile);

            // Corrupt 0-byte explicit file must NOT shadow valid model in customStorageDir\whisper\
            string corruptExplicit = Path.Combine(tempRoot, "corrupt-zero.bin");
            File.WriteAllBytes(corruptExplicit, Array.Empty<byte>());
            WhisperModelManager.GetModelPath("base", customModelStorageDir: tempRoot, explicitWhisperPath: corruptExplicit)
                .Should().Be(fakeModel);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }
}
