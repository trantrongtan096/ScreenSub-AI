using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Ocr;

public sealed class PaddleOcrEngine : IOcrEngine
{
    private readonly string _pythonExePath;
    private readonly string _workerScriptPath;
    private readonly TextNormalizer _normalizer;
    private Process? _workerProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private int _requestIdCounter;
    private bool _isDisposed;

    public string EngineName => "PaddleOCR (Local)";
    public ServiceStatus Status { get; private set; } = ServiceStatus.Stopped;
    public string StatusMessage { get; private set; } = "Chưa khởi động";
    public bool IsPaddleAvailable { get; private set; }
    public double LastDecodeMs { get; private set; }
    public double LastDetMs { get; private set; }
    public double LastRecMs { get; private set; }

    public PaddleOcrEngine(string pythonExePath = "python", string workerScriptPath = "workers/paddle-ocr/ocr_worker.py")
    {
        _pythonExePath = pythonExePath;
        _workerScriptPath = workerScriptPath;
        _normalizer = new TextNormalizer();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            if (Status == ServiceStatus.Running && _workerProcess != null && !_workerProcess.HasExited)
            {
                return;
            }

            if (_workerProcess != null)
            {
                try
                {
                    if (!_workerProcess.HasExited) _workerProcess.Kill(entireProcessTree: true);
                    _workerProcess.Dispose();
                }
                catch { }
                _workerProcess = null;
            }

            Status = ServiceStatus.Starting;
            StatusMessage = "Đang khởi động OCR worker...";

            string fullScriptPath = ResolveScriptPath();

            if (!File.Exists(fullScriptPath))
            {
                Status = ServiceStatus.Error;
                StatusMessage = $"Không tìm thấy worker script: {fullScriptPath}";
                return;
            }

            string pythonExe = ResolvePythonPath(_pythonExePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{fullScriptPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                StandardInputEncoding = new System.Text.UTF8Encoding(false)
            };

            _workerProcess = new Process { StartInfo = startInfo };
            _workerProcess.EnableRaisingEvents = true;
            _workerProcess.Exited += (_, _) =>
            {
                if (!_isDisposed)
                {
                    Status = ServiceStatus.Error;
                    StatusMessage = "Tiến trình OCR worker đã thoát bất ngờ.";
                }
            };

            try
            {
                _workerProcess.Start();
                AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                {
                    try
                    {
                        if (_workerProcess != null && !_workerProcess.HasExited)
                        {
                            _workerProcess.Kill();
                        }
                    }
                    catch { }
                };
            }
            catch (Exception ex)
            {
                Status = ServiceStatus.Error;
                StatusMessage = $"Không thể khởi chạy Python: {ex.Message}";
                return;
            }

            _stdin = _workerProcess.StandardInput;
            _stdout = _workerProcess.StandardOutput;

            // Ping worker with a generous startup timeout (ONNX Runtime import can take several seconds under load)
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            pingCts.CancelAfter(TimeSpan.FromSeconds(15));

            int id = Interlocked.Increment(ref _requestIdCounter);
            var pingReq = JsonSerializer.Serialize(new { id, cmd = "ping" });
            await _stdin.WriteLineAsync(pingReq.AsMemory(), pingCts.Token);
            await _stdin.FlushAsync(pingCts.Token);

            var responseLine = await _stdout.ReadLineAsync(pingCts.Token);
            if (!string.IsNullOrWhiteSpace(responseLine))
            {
                using var doc = JsonDocument.Parse(responseLine);
                string backend = "unknown";
                if (doc.RootElement.TryGetProperty("backend", out var bProp))
                {
                    backend = bProp.GetString() ?? "unknown";
                }

                if (doc.RootElement.TryGetProperty("paddle", out var paddleProp))
                {
                    IsPaddleAvailable = paddleProp.GetBoolean();
                }

                if (IsPaddleAvailable)
                {
                    Status = ServiceStatus.Running;
                    StatusMessage = backend switch
                    {
                        "rapidocr" => "Sẵn sàng (RapidOCR ONNX)",
                        "paddleocr" => "Sẵn sàng (PaddleOCR)",
                        _ => "Sẵn sàng"
                    };
                }
                else
                {
                    Status = ServiceStatus.Degraded;
                    StatusMessage = "Chưa cài OCR: Cần chạy pip install rapidocr_onnxruntime";
                }
            }
            else
            {
                Status = ServiceStatus.Error;
                StatusMessage = "Worker không phản hồi lệnh ping";
            }
        }
        catch (OperationCanceledException)
        {
            Status = ServiceStatus.Error;
            StatusMessage = "Khởi động worker bị timeout";
        }
        catch (Exception ex)
        {
            Status = ServiceStatus.Error;
            StatusMessage = $"Lỗi khởi động OCR: {ex.Message}";
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default)
    {
        if (imageBytes == null || imageBytes.Length == 0)
        {
            return OcrResult.Empty;
        }

        if (Status != ServiceStatus.Running || _workerProcess == null || _workerProcess.HasExited)
        {
            try
            {
                await InitializeAsync(ct);
            }
            catch
            {
                return OcrResult.Empty;
            }

            if (Status != ServiceStatus.Running)
            {
                return OcrResult.Empty;
            }
        }

        if (!await _semaphore.WaitAsync(10000, ct))
        {
            return OcrResult.Empty;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            int reqId = Interlocked.Increment(ref _requestIdCounter);
            string base64Image = Convert.ToBase64String(imageBytes);

            var reqObj = new
            {
                id = reqId,
                cmd = "ocr",
                image_base64 = base64Image
            };

            string jsonLine = JsonSerializer.Serialize(reqObj);
            if (_stdin == null || _stdout == null)
            {
                return OcrResult.Empty;
            }

            await _stdin.WriteLineAsync(jsonLine.AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            // Wait for the response matching reqId before honoring caller cancellation,
            // so mid-flight Stop/Pause/RegionSwitch never leaves unread JSON responses desynchronizing stdout.
            using var ioTimeoutCts = new CancellationTokenSource(15000);
            string? respLine = null;
            JsonDocument? matchedDoc = null;
            while (true)
            {
                respLine = await _stdout.ReadLineAsync(ioTimeoutCts.Token);
                if (respLine == null)
                {
                    ResetProcessUnlocked();
                    return OcrResult.Empty;
                }
                if (string.IsNullOrWhiteSpace(respLine))
                {
                    continue;
                }

                JsonDocument candidateDoc;
                try
                {
                    candidateDoc = JsonDocument.Parse(respLine);
                }
                catch
                {
                    ResetProcessUnlocked();
                    return OcrResult.Empty;
                }

                if (candidateDoc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                {
                    int respId = idProp.GetInt32();
                    if (respId != reqId && respId > 0 && respId < reqId)
                    {
                        candidateDoc.Dispose();
                        continue;
                    }
                }

                matchedDoc = candidateDoc;
                break;
            }

            sw.Stop();

            if (ct.IsCancellationRequested)
            {
                matchedDoc?.Dispose();
                return OcrResult.Empty;
            }

            using var doc = matchedDoc!;
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "ok")
            {
                var ocrLines = new List<OcrLine>();
                if (root.TryGetProperty("lines", out var linesProp) && linesProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in linesProp.EnumerateArray())
                    {
                        ocrLines.Add(new OcrLine
                        {
                            Text = item.GetProperty("text").GetString() ?? string.Empty,
                            Confidence = item.GetProperty("confidence").GetDouble(),
                            Left = item.GetProperty("left").GetInt32(),
                            Top = item.GetProperty("top").GetInt32(),
                            Right = item.GetProperty("right").GetInt32(),
                            Bottom = item.GetProperty("bottom").GetInt32()
                        });
                    }
                }

                string mergedText = _normalizer.NormalizeAndMergeLines(ocrLines);
                var (stableText, _) = _normalizer.ApplyStabilityFilter(mergedText);
                string finalText = !string.IsNullOrWhiteSpace(stableText) ? stableText : mergedText;

                double avgConf = ocrLines.Count > 0 ? ocrLines.Average(l => l.Confidence) : 0;
                LastDecodeMs = root.TryGetProperty("decode_ms", out var decP) ? decP.GetDouble() : 0;
                LastDetMs = root.TryGetProperty("det_ms", out var detP) ? detP.GetDouble() : 0;
                LastRecMs = root.TryGetProperty("rec_ms", out var recP) ? recP.GetDouble() : 0;
                long latency = root.TryGetProperty("latency_ms", out var latProp) ? latProp.GetInt64() : sw.ElapsedMilliseconds;

                return new OcrResult
                {
                    FullText = finalText,
                    Lines = ocrLines,
                    Confidence = avgConf,
                    LatencyMs = latency,
                    Timestamp = DateTime.UtcNow
                };
            }

            return OcrResult.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return OcrResult.Empty;
        }
        catch
        {
            ResetProcessUnlocked();
            return OcrResult.Empty;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private void ResetProcessUnlocked()
    {
        try { _stdin?.Dispose(); } catch { }
        try { _stdout?.Dispose(); } catch { }
        if (_workerProcess != null)
        {
            try
            {
                if (!_workerProcess.HasExited) _workerProcess.Kill(entireProcessTree: true);
            }
            catch { }
            try { _workerProcess.Dispose(); } catch { }
        }
        _stdin = null;
        _stdout = null;
        _workerProcess = null;
        if (!_isDisposed)
        {
            Status = ServiceStatus.Stopped;
        }
    }

    private string ResolveScriptPath()
    {
        if (Path.IsPathRooted(_workerScriptPath) && File.Exists(_workerScriptPath))
        {
            return _workerScriptPath;
        }

        string inBase = Path.Combine(AppContext.BaseDirectory, _workerScriptPath);
        if (File.Exists(inBase))
        {
            return inBase;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, _workerScriptPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            string altCandidate = Path.Combine(dir.FullName, "workers", "paddle-ocr", "ocr_worker.py");
            if (File.Exists(altCandidate))
            {
                return altCandidate;
            }
            dir = dir.Parent;
        }

        string inCwd = Path.Combine(Directory.GetCurrentDirectory(), _workerScriptPath);
        if (File.Exists(inCwd))
        {
            return inCwd;
        }

        return inBase;
    }

    private static string ResolvePythonPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            return configuredPath;
        }

        // Check app-bundled portable Python runtime first, then well-known system install paths
        string baseDir = AppContext.BaseDirectory;
        string[] wellKnown =
        [
            Path.Combine(baseDir, "python", "python.exe"),
            Path.Combine(baseDir, "runtime", "python", "python.exe"),
            Path.Combine(baseDir, ".venv", "Scripts", "python.exe"),
            @"C:\PyThon\python.exe",
            @"C:\Program Files\Python312\python.exe",
            @"C:\Program Files\Python311\python.exe",
            @"C:\Program Files\Python310\python.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python312\python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python311\python.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python310\python.exe")
        ];

        foreach (var p in wellKnown)
        {
            if (File.Exists(p)) return p;
        }

        // Check PATH environment variable, excluding WindowsApps
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            var paths = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in paths)
            {
                if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = Path.Combine(dir, "python.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return configuredPath;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_stdin != null)
            {
                var quitReq = JsonSerializer.Serialize(new { cmd = "quit" });
                await _stdin.WriteLineAsync(quitReq);
                await _stdin.FlushAsync();
            }

            if (_workerProcess != null && !_workerProcess.HasExited)
            {
                _workerProcess.Kill(entireProcessTree: true);
                _workerProcess.Dispose();
            }
        }
        catch { }

        _semaphore.Dispose();
    }
}
