using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface ISpeechRecognitionService : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<SpeechRecognitionResult> RecognizeAsync(byte[] pcm16Mono16kData, string language = "auto", CancellationToken ct = default);

    bool IsModelLoaded { get; }
    string CurrentModelName { get; }
    ServiceStatus Status { get; }
    string StatusMessage { get; }
}

