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
    public bool IsPaddleAvailable { get; private set; }

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
            if (_workerProcess != null && !_workerProcess.HasExited)
            {
                return;
            }

            Status = ServiceStatus.Starting;

            string fullScriptPath = Path.IsPathRooted(_workerScriptPath)
                ? _workerScriptPath
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, _workerScriptPath));

            if (!File.Exists(fullScriptPath))
            {
                // Also check relative to workspace
                var altPath = Path.GetFullPath(_workerScriptPath);
                if (File.Exists(altPath))
                {
                    fullScriptPath = altPath;
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _pythonExePath,
                Arguments = $"\"{fullScriptPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = System.Text.Encoding.UTF8
            };

            _workerProcess = new Process { StartInfo = startInfo };
            _workerProcess.EnableRaisingEvents = true;
            _workerProcess.Exited += (_, _) =>
            {
                if (!_isDisposed)
                {
                    Status = ServiceStatus.Error;
                }
            };

            _workerProcess.Start();
            _stdin = _workerProcess.StandardInput;
            _stdout = _workerProcess.StandardOutput;

            // Ping worker
            int id = Interlocked.Increment(ref _requestIdCounter);
            var pingReq = JsonSerializer.Serialize(new { id, cmd = "ping" });
            await _stdin.WriteLineAsync(pingReq.AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            var responseLine = await _stdout.ReadLineAsync(ct);
            if (!string.IsNullOrWhiteSpace(responseLine))
            {
                using var doc = JsonDocument.Parse(responseLine);
                if (doc.RootElement.TryGetProperty("paddle", out var paddleProp))
                {
                    IsPaddleAvailable = paddleProp.GetBoolean();
                }
                Status = ServiceStatus.Running;
            }
            else
            {
                Status = ServiceStatus.Degraded;
            }
        }
        catch
        {
            Status = ServiceStatus.Error;
            throw;
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
        }

        // Use lock so requests are sent sequentially without interleaved lines
        if (!await _semaphore.WaitAsync(2000, ct))
        {
            // Drop frame if OCR is overloaded
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

            var respLine = await _stdout.ReadLineAsync(ct);
            sw.Stop();

            if (string.IsNullOrWhiteSpace(respLine))
            {
                return OcrResult.Empty;
            }

            using var doc = JsonDocument.Parse(respLine);
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

                double avgConf = ocrLines.Count > 0 ? ocrLines.Average(l => l.Confidence) : 0;
                long latency = root.TryGetProperty("latency_ms", out var latProp) ? latProp.GetInt64() : sw.ElapsedMilliseconds;

                return new OcrResult
                {
                    FullText = stableText,
                    Lines = ocrLines,
                    Confidence = avgConf,
                    LatencyMs = latency,
                    Timestamp = DateTime.UtcNow
                };
            }

            return OcrResult.Empty;
        }
        catch
        {
            return OcrResult.Empty;
        }
        finally
        {
            _semaphore.Release();
        }
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
