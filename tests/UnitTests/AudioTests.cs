using FluentAssertions;
using ScreenSub.Audio.Capture;
using ScreenSub.Audio.Processing;
using ScreenSub.Audio.VAD;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;
using Xunit;

namespace UnitTests;

public class AudioTests
{
    [Fact]
    public void AudioResampler_Resample48kFloatStereoTo16kMonoPcm_ShouldProduceCorrectLengthAndFormat()
    {
        // 48,000 Hz, stereo (2 channels), 32-bit float = 8 bytes per frame
        // 1 second of 48kHz audio = 48,000 frames = 384,000 bytes
        int sourceSampleRate = 48000;
        int sourceChannels = 2;
        int bytesRecorded = 384000;
        byte[] sourceBuffer = new byte[bytesRecorded];

        // Fill with a sine wave at 440 Hz
        for (int i = 0; i < 48000; i++)
        {
            float sample = (float)Math.Sin(2 * Math.PI * 440 * i / sourceSampleRate);
            byte[] sampleBytes = BitConverter.GetBytes(sample);
            Array.Copy(sampleBytes, 0, sourceBuffer, i * 8, 4);     // Left channel
            Array.Copy(sampleBytes, 0, sourceBuffer, i * 8 + 4, 4); // Right channel
        }

        byte[] pcm16 = AudioResampler.ResampleTo16kMonoPcm(
            sourceBuffer,
            bytesRecorded,
            sourceSampleRate,
            sourceChannels,
            sourceBitsPerSample: 32,
            isFloat: true);

        // Target: 16,000 Hz mono 16-bit PCM = 16,000 samples * 2 bytes = 32,000 bytes
        pcm16.Should().NotBeNull();
        pcm16.Length.Should().Be(32000);

        // Verify it contains non-zero sound data
        short firstSample = BitConverter.ToInt16(pcm16, 200);
        firstSample.Should().NotBe(0);
    }

    [Fact]
    public void EnergyVoiceActivityDetector_Silence_ShouldRemainSilence()
    {
        var vad = new EnergyVoiceActivityDetector();
        byte[] silentChunk = new byte[640]; // 20 ms at 16kHz 16-bit mono

        var state = vad.ProcessAudioChunk(silentChunk);

        state.Should().Be(VadState.Silence);
        vad.CurrentState.Should().Be(VadState.Silence);
    }

    [Fact]
    public void EnergyVoiceActivityDetector_SpeechBurst_ShouldTransitionToSpeechAndEmitSegment()
    {
        var vad = new EnergyVoiceActivityDetector(preRollMs: 100);
        vad.SilenceThreshold = 0.01;
        vad.MinSpeechDurationMs = 150;
        vad.HangoverMs = 200;

        SpeechSegment? emittedSegment = null;
        vad.SpeechSegmentReady += (_, seg) => emittedSegment = seg;

        // 1. Send silent frames (100 ms)
        byte[] silentChunk = new byte[640];
        for (int i = 0; i < 5; i++)
        {
            vad.ProcessAudioChunk(silentChunk);
        }

        // 2. Generate loud speech chunk (440Hz sine wave, amplitude 0.3)
        byte[] speechChunk = new byte[640];
        for (int i = 0; i < 320; i++)
        {
            short sample = (short)(Math.Sin(2 * Math.PI * 440 * i / 16000) * 10000);
            speechChunk[i * 2] = (byte)(sample & 0xFF);
            speechChunk[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        // Send 300 ms of speech (15 chunks * 20 ms)
        for (int i = 0; i < 15; i++)
        {
            vad.ProcessAudioChunk(speechChunk);
        }

        vad.CurrentState.Should().BeOneOf(VadState.SpeechDetected, VadState.SpeechInProgress);

        // 3. Send 300 ms of silence to expire hangover (15 chunks * 20 ms)
        Thread.Sleep(250); // Advance real time for hangover calculation
        for (int i = 0; i < 15; i++)
        {
            vad.ProcessAudioChunk(silentChunk);
        }

        // Segment should have been emitted
        emittedSegment.Should().NotBeNull();
        emittedSegment!.PcmData.Length.Should().BeGreaterThan(0);
        emittedSegment.DurationMs.Should().BeGreaterThan(100);
    }

    [Fact]
    public void WasapiAudioCaptureService_GetOutputDevices_ShouldReturnDeviceList()
    {
        using var service = new WasapiAudioCaptureService();
        var devices = service.GetOutputDevices();

        devices.Should().NotBeNull();
        // On any Windows machine with an active audio driver, at least 1 device is enumerated
        // If headless/virtual, devices list is valid empty or populated
    }
}

