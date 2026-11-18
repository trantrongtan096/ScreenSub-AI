using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenSub.Audio.Processing;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Audio.Capture;

public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    private WasapiLoopbackCapture? _capture;
    private MMDevice? _selectedDevice;
    private readonly object _lock = new();
    private bool _isCapturing;
    private bool _isPaused;
    private DateTime _lastLevelEventTime = DateTime.MinValue;
    private const int LevelUpdateIntervalMs = 40; // 25 FPS update rate for smooth meter without UI lag

    public bool IsCapturing => _isCapturing && !_isPaused;
    public float CurrentPeakLevel { get; private set; }
    public float CurrentRmsLevel { get; private set; }
    public AudioDeviceInfo? SelectedDevice { get; private set; }

    public event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<string>? DeviceChanged;
    public event EventHandler<string>? CaptureError;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? defaultDevice = null;
            try
            {
                defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { }

            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var ep in endpoints)
            {
                bool isDefault = defaultDevice != null && ep.ID == defaultDevice.ID;
                devices.Add(new AudioDeviceInfo(ep.ID, ep.FriendlyName, isDefault));
            }
        }
        catch (Exception ex)
        {
            CaptureError?.Invoke(this, $"Không thể liệt kê thiết bị âm thanh: {ex.Message}");
        }

        return devices;
    }

    public void SetSelectedDevice(AudioDeviceInfo? device)
    {
        lock (_lock)
        {
            SelectedDevice = device;
            _selectedDevice?.Dispose();
            _selectedDevice = null;

            if (device != null)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    _selectedDevice = enumerator.GetDevice(device.Id);
                }
                catch (Exception ex)
                {
                    CaptureError?.Invoke(this, $"Lỗi chọn thiết bị {device.Name}: {ex.Message}");
                }
            }

            if (_isCapturing)
            {
                // Restart capture on newly selected device
                StopCaptureInternal();
                StartCaptureInternal();
            }

            DeviceChanged?.Invoke(this, SelectedDevice?.Name ?? "Mặc định (Default System Audio)");
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_isCapturing && !_isPaused) return;

            if (_isPaused)
            {
                _isPaused = false;
                return;
            }

            StartCaptureInternal();
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            _isPaused = true;
            CurrentPeakLevel = 0f;
            CurrentRmsLevel = 0f;
            AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(0f, 0f, -96f, false));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            StopCaptureInternal();
        }
    }

    private void StartCaptureInternal()
    {
        try
        {
            // If user selected a specific device, capture from it; otherwise use default loopback
            _capture = _selectedDevice != null
                ? new WasapiLoopbackCapture(_selectedDevice)
                : new WasapiLoopbackCapture();

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            _capture.StartRecording();
            _isCapturing = true;
            _isPaused = false;
        }
        catch (Exception ex)
        {
            _isCapturing = false;
            CaptureError?.Invoke(this, $"Không thể khởi động thu âm thanh WASAPI: {ex.Message}");
        }
    }

    private void StopCaptureInternal()
    {
        _isCapturing = false;
        _isPaused = false;
        CurrentPeakLevel = 0f;
        CurrentRmsLevel = 0f;

        if (_capture != null)
        {
            try
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch { }
            finally
            {
                _capture = null;
            }
        }

        AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(0f, 0f, -96f, false));
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isCapturing || _isPaused || e.BytesRecorded <= 0)
        {
            return;
        }

        var waveFormat = _capture?.WaveFormat;
        if (waveFormat == null) return;

        bool isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;
        int bitsPerSample = waveFormat.BitsPerSample;
        int sampleRate = waveFormat.SampleRate;
        int channels = waveFormat.Channels;

        // 1. Calculate Peak and RMS Level for audio meter
        float peak = 0f;
        double sumSquares = 0.0;
        int sampleCount = 0;

        if (isFloat && bitsPerSample == 32)
        {
            for (int i = 0; i < e.BytesRecorded; i += 4)
            {
                float val = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
                if (val > peak) peak = val;
                sumSquares += val * val;
                sampleCount++;
            }
        }
        else if (bitsPerSample == 16)
        {
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                float val = Math.Abs(BitConverter.ToInt16(e.Buffer, i)) / 32768f;
                if (val > peak) peak = val;
                sumSquares += val * val;
                sampleCount++;
            }
        }

        float rms = sampleCount > 0 ? (float)Math.Sqrt(sumSquares / sampleCount) : 0f;
        peak = Math.Clamp(peak, 0f, 1f);
        rms = Math.Clamp(rms, 0f, 1f);

        CurrentPeakLevel = peak;
        CurrentRmsLevel = rms;

        var now = DateTime.UtcNow;
        if ((now - _lastLevelEventTime).TotalMilliseconds >= LevelUpdateIntervalMs)
        {
            _lastLevelEventTime = now;
            float db = rms > 0.00001f ? (float)(20.0 * Math.Log10(rms)) : -96f;
            db = Math.Clamp(db, -96f, 0f);
            bool hasSound = rms > 0.002f;

            AudioLevelChanged?.Invoke(this, new AudioLevelEventArgs(peak, rms, db, hasSound));
        }

        // 2. Resample raw audio to 16kHz 16-bit Mono PCM
        byte[] pcm16Mono = AudioResampler.ResampleTo16kMonoPcm(
            e.Buffer,
            e.BytesRecorded,
            sampleRate,
            channels,
            bitsPerSample,
            isFloat);

        if (pcm16Mono.Length > 0)
        {
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs(pcm16Mono, 16000, 1));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null && _isCapturing)
        {
            CaptureError?.Invoke(this, $"Lỗi thiết bị thu âm thanh: {e.Exception.Message}");
        }
    }

    public void Dispose()
    {
        StopCaptureInternal();
        _selectedDevice?.Dispose();
        _selectedDevice = null;
    }
}
