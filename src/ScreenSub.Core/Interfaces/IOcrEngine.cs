using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;

namespace ScreenSub.Core.Interfaces;

public interface IOcrEngine : IAsyncDisposable
{
    string EngineName { get; }
    ServiceStatus Status { get; }
    Task InitializeAsync(CancellationToken ct = default);
    Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default);
}
