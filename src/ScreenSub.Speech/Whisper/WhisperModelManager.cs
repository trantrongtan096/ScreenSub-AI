using System.IO;
using Whisper.net.Ggml;

namespace ScreenSub.Speech.Whisper;

public static class WhisperModelManager
{
    public static string DefaultModelDirectory
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(appData, "ScreenSubAI", "Models", "whisper");
        }
    }

    private static bool IsValidModelFile(string path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) && new FileInfo(path).Length > 1024 * 1024;

    public static string GetModelPath(
        string modelType = "base",
        string? customModelStorageDir = null,
        string? explicitWhisperPath = null)
    {
        string fileName = $"ggml-{modelType.ToLowerInvariant()}.bin";

        // 0. Check explicit user-configured file path if rooted and valid
        if (Path.IsPathRooted(explicitWhisperPath) && IsValidModelFile(explicitWhisperPath!))
        {
            return explicitWhisperPath!;
        }

        // 1. Respect explicit ModelStorageDirectory isolation (matches LocalModelManager.GetModelStorageDirectory)
        if (!string.IsNullOrWhiteSpace(customModelStorageDir))
        {
            string customWhisper = Path.Combine(customModelStorageDir, "whisper", fileName);
            if (IsValidModelFile(customWhisper)) return customWhisper;

            string customDirect = Path.Combine(customModelStorageDir, fileName);
            if (IsValidModelFile(customDirect)) return customDirect;

            return customWhisper;
        }

        // 2. Check local models folder in app directory
        string localPath = Path.Combine(AppContext.BaseDirectory, "models", "whisper", fileName);
        if (IsValidModelFile(localPath)) return localPath;

        // 3. Check canonical AppData\ScreenSubAI\Models\whisper\ (matches LocalModelManager)
        string appDataPath = Path.Combine(DefaultModelDirectory, fileName);
        if (IsValidModelFile(appDataPath)) return appDataPath;

        // 4. Backward-compatible fallback for legacy AppData\ScreenSubAI\models\ggml-*.bin
        string legacyAppDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenSubAI", "models", fileName);
        if (IsValidModelFile(legacyAppDataPath)) return legacyAppDataPath;

        // 5. Check workspace directory
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, "models", "whisper", fileName);
            if (IsValidModelFile(candidate)) return candidate;
            dir = dir.Parent;
        }

        return appDataPath;
    }

    public static bool IsModelAvailable(
        string modelType = "base",
        string? customModelStorageDir = null,
        string? explicitWhisperPath = null)
    {
        string path = GetModelPath(modelType, customModelStorageDir, explicitWhisperPath);
        return IsValidModelFile(path);
    }

    public static async Task<string> DownloadModelAsync(
        GgmlType modelType = GgmlType.Base,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        string targetDir = DefaultModelDirectory;
        Directory.CreateDirectory(targetDir);

        string fileName = $"ggml-{modelType.ToString().ToLowerInvariant()}.bin";
        string targetPath = Path.Combine(targetDir, fileName);

        if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 10 * 1024 * 1024)
        {
            return targetPath;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        var downloader = new WhisperGgmlDownloader(httpClient);
        using var modelStream = await downloader.GetGgmlModelAsync(modelType, cancellationToken: ct);
        using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

        byte[] buffer = new byte[81920];
        int bytesRead;
        long totalRead = 0;

        while ((bytesRead = await modelStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            // Approximate progress based on typical model sizes (tiny ~75MB, base ~145MB)
            double expectedSize = modelType == GgmlType.Tiny ? 75.0 * 1024 * 1024 : 145.0 * 1024 * 1024;
            progress?.Report(Math.Min(1.0, totalRead / expectedSize));
        }

        return targetPath;
    }
}
