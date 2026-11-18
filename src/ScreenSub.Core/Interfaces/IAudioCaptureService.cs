using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface IAudioCaptureService : IDisposable
{
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    AudioDeviceInfo? SelectedDevice { get; }
    void SetSelectedDevice(AudioDeviceInfo? device);

    void Start();
    void Pause();
    void Stop();

    bool IsCapturing { get; }
    float CurrentPeakLevel { get; }
    float CurrentRmsLevel { get; }

    event EventHandler<AudioLevelEventArgs>? AudioLevelChanged;
    event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    event EventHandler<string>? DeviceChanged;
    event EventHandler<string>? CaptureError;
}

