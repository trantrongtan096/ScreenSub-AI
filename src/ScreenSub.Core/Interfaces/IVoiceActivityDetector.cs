using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface IVoiceActivityDetector
{
    VadState ProcessAudioChunk(byte[] pcmChunk, int sampleRate = 16000);
    void Reset();

    VadState CurrentState { get; }
    double SilenceThreshold { get; set; }
    int MinSpeechDurationMs { get; set; }
    int MaxSpeechDurationMs { get; set; }
    int HangoverMs { get; set; }

    event EventHandler<SpeechSegment>? SpeechSegmentReady;
    event EventHandler<VadState>? StateChanged;
}

