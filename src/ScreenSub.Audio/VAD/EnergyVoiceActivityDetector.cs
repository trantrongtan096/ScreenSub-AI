using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Audio.VAD;

/// <summary>
/// Energy-based Voice Activity Detector (VAD) with rolling pre-roll buffer and hangover hangover timing.
/// Detects speech boundaries in 16kHz 16-bit Mono PCM streams.
/// </summary>
public sealed class EnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly object _lock = new();
    private readonly MemoryStream _speechBuffer = new();
    private readonly Queue<byte[]> _preRollQueue = new();
    private int _preRollBytesCount = 0;
    private readonly int _maxPreRollBytes;

    private long _segmentCounter = 0;
    private string _currentSessionId = Guid.NewGuid().ToString("N")[..8];
    private DateTime _speechStartTime = DateTime.MinValue;
    private DateTime _lastSpeechTime = DateTime.MinValue;
    private double _streamTimeMs = 0;
    private double _speechStartTimeMs = 0;
    private double _lastSpeechTimeMs = 0;

    public VadState CurrentState { get; private set; } = VadState.Silence;
    public double SilenceThreshold { get; set; } = 0.012; // Energy threshold for speech detection
    public int MinSpeechDurationMs { get; set; } = 300;   // Minimum speech duration to trigger ASR
    public int MaxSpeechDurationMs { get; set; } = 6000;  // Maximum segment duration before splitting
    public int HangoverMs { get; set; } = 500;            // Hangover duration after speech pause

    public event EventHandler<SpeechSegment>? SpeechSegmentReady;
    public event EventHandler<VadState>? StateChanged;

    public EnergyVoiceActivityDetector(int preRollMs = 300)
    {
        // At 16kHz 16-bit Mono: 1 second = 32,000 bytes; 1 ms = 32 bytes
        _maxPreRollBytes = Math.Max(3200, preRollMs * 32);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _speechBuffer.SetLength(0);
            _preRollQueue.Clear();
            _preRollBytesCount = 0;
            _speechStartTime = DateTime.MinValue;
            _lastSpeechTime = DateTime.MinValue;
            _streamTimeMs = 0;
            _speechStartTimeMs = 0;
            _lastSpeechTimeMs = 0;
            _currentSessionId = Guid.NewGuid().ToString("N")[..8];

            if (CurrentState != VadState.Silence)
            {
                CurrentState = VadState.Silence;
                StateChanged?.Invoke(this, CurrentState);
            }
        }
    }

    public VadState ProcessAudioChunk(byte[] pcmChunk, int sampleRate = 16000)
    {
        if (pcmChunk == null || pcmChunk.Length < 2)
        {
            return CurrentState;
        }

        lock (_lock)
        {
            float rms = CalculatePcm16Rms(pcmChunk);
            bool isVoiceEnergy = rms >= SilenceThreshold;
            var now = DateTime.UtcNow;

            double chunkDurationMs = (double)pcmChunk.Length / (sampleRate * 2) * 1000.0;
            _streamTimeMs += chunkDurationMs;

            switch (CurrentState)
            {
                case VadState.Silence:
                    if (isVoiceEnergy)
                    {
                        // Transition: Silence -> SpeechDetected
                        CurrentState = VadState.SpeechDetected;
                        _speechStartTime = now;
                        _lastSpeechTime = now;
                        _speechStartTimeMs = _streamTimeMs - chunkDurationMs;
                        _lastSpeechTimeMs = _streamTimeMs;
                        StateChanged?.Invoke(this, CurrentState);

                        // Flush pre-roll buffer into active speech buffer
                        _speechBuffer.SetLength(0);
                        while (_preRollQueue.Count > 0)
                        {
                            var preChunk = _preRollQueue.Dequeue();
                            _speechBuffer.Write(preChunk, 0, preChunk.Length);
                        }
                        _preRollBytesCount = 0;

                        // Append current chunk
                        _speechBuffer.Write(pcmChunk, 0, pcmChunk.Length);
                    }
                    else
                    {
                        // Maintain rolling pre-roll buffer during silence
                        _preRollQueue.Enqueue(pcmChunk);
                        _preRollBytesCount += pcmChunk.Length;
                        while (_preRollBytesCount > _maxPreRollBytes && _preRollQueue.Count > 0)
                        {
                            var old = _preRollQueue.Dequeue();
                            _preRollBytesCount -= old.Length;
                        }
                    }
                    break;

                case VadState.SpeechDetected:
                case VadState.SpeechInProgress:
                    _speechBuffer.Write(pcmChunk, 0, pcmChunk.Length);

                    if (isVoiceEnergy)
                    {
                        _lastSpeechTime = now;
                        _lastSpeechTimeMs = _streamTimeMs;
                        if (CurrentState == VadState.SpeechDetected && Math.Max((now - _speechStartTime).TotalMilliseconds, _streamTimeMs - _speechStartTimeMs) >= 100)
                        {
                            CurrentState = VadState.SpeechInProgress;
                            StateChanged?.Invoke(this, CurrentState);
                        }
                    }

                    double speechDuration = Math.Max((now - _speechStartTime).TotalMilliseconds, _streamTimeMs - _speechStartTimeMs);
                    double silenceDuration = Math.Max((now - _lastSpeechTime).TotalMilliseconds, _streamTimeMs - _lastSpeechTimeMs);

                    // Condition A: Silence hangover exceeded
                    bool hangoverExpired = silenceDuration >= HangoverMs;

                    // Condition B: Reached maximum segment duration (chunk long speech)
                    bool maxDurationReached = speechDuration >= MaxSpeechDurationMs;

                    if (hangoverExpired || maxDurationReached)
                    {
                        // Conclude segment
                        if (speechDuration >= MinSpeechDurationMs && _speechBuffer.Length > 0)
                        {
                            var segment = new SpeechSegment
                            {
                                Id = Interlocked.Increment(ref _segmentCounter),
                                SessionId = _currentSessionId,
                                StartTimeMs = (long)_speechStartTime.TimeOfDay.TotalMilliseconds,
                                EndTimeMs = (long)now.TimeOfDay.TotalMilliseconds,
                                PcmData = _speechBuffer.ToArray(),
                                SampleRate = sampleRate
                            };

                            SpeechSegmentReady?.Invoke(this, segment);
                        }

                        // Reset for next segment
                        _speechBuffer.SetLength(0);

                        if (maxDurationReached && isVoiceEnergy)
                        {
                            // Continuous speech without pause: keep in SpeechInProgress
                            _speechStartTime = now;
                            _lastSpeechTime = now;
                            _speechStartTimeMs = _streamTimeMs;
                            _lastSpeechTimeMs = _streamTimeMs;
                            CurrentState = VadState.SpeechInProgress;
                        }
                        else
                        {
                            CurrentState = VadState.SpeechEnded;
                            StateChanged?.Invoke(this, CurrentState);

                            // Transition back to silence
                            CurrentState = VadState.Silence;
                            StateChanged?.Invoke(this, CurrentState);
                        }
                    }
                    break;

                case VadState.SpeechEnded:
                    CurrentState = VadState.Silence;
                    StateChanged?.Invoke(this, CurrentState);
                    break;
            }

            return CurrentState;
        }
    }

    private static float CalculatePcm16Rms(byte[] pcm16)
    {
        double sumSquares = 0.0;
        int count = pcm16.Length / 2;
        if (count == 0) return 0f;

        for (int i = 0; i < pcm16.Length; i += 2)
        {
            short sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            float norm = sample / 32768f;
            sumSquares += norm * norm;
        }

        return (float)Math.Sqrt(sumSquares / count);
    }
}

