using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Translation;

public sealed record NeuralTranslationResponse(
    string TranslatedText,
    string RawTranslatedText,
    string ModelUsed,
    string ModelName,
    string ModelFingerprint,
    bool FallbackUsed,
    string DecodingConfig,
    double ModelLoadMs,
    double TokenizeMs,
    double InferMs,
    double PostprocMs,
    long TotalLatencyMs);

/// <summary>
/// Real offline neural translation engine powered by CTranslate2 INT8 models
/// (M2M-100 418M baseline/rollback + bounded multi-model router for EnViT5 / Opus-MT Zh-Vi).
/// Runs 100% locally on Windows without network calls or cloud/mock fallbacks.
/// </summary>
public sealed class LocalNeuralTranslationEngine : IDisposable, IAsyncDisposable
{
    public const string ModelIdM2M100 = "m2m100_418m_ct2";
    public const string ModelIdEnViT5 = "envit5_ct2_int8";
    public const string ModelIdOpusZhVi = "opus_mt_zh_vi_ct2";

    private readonly string _pythonExePath;
    private readonly string _workerScriptPath;
    private readonly ISettingsService? _settingsService;
    private readonly Dictionary<string, string> _knownFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private Process? _workerProcess;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _requestIdCounter;
    private bool _isDisposed;
    private int? _explicitBeamSize;

    private bool? _enableMultiModelRouter;
    public bool EnableMultiModelRouter
    {
        get => _enableMultiModelRouter ?? _settingsService?.Settings.EnableOfflineMultiModelRouter ?? false;
        set => _enableMultiModelRouter = value;
    }

    private bool? _enableExperimentalZhViOpusModel;
    public bool EnableExperimentalZhViOpusModel
    {
        get => _enableExperimentalZhViOpusModel ?? _settingsService?.Settings.EnableExperimentalZhViOpusModel ?? true;
        set => _enableExperimentalZhViOpusModel = value;
    }

    private bool? _allowSpecializedFallbackToM2M100;
    public bool AllowSpecializedFallbackToM2M100
    {
        get => _allowSpecializedFallbackToM2M100 ?? _settingsService?.Settings.AllowOfflineSpecializedFallbackToM2M100 ?? true;
        set => _allowSpecializedFallbackToM2M100 = value;
    }

    private int? _maxLoadedModelsInMemory;
    public int MaxLoadedModelsInMemory
    {
        get => Math.Max(1, _maxLoadedModelsInMemory ?? _settingsService?.Settings.MaxLoadedTranslationModelsInMemory ?? 1);
        set => _maxLoadedModelsInMemory = Math.Max(1, value);
    }

    public string? CustomModelsRoot { get; set; }
    public bool SkipStartupAutoLoad { get; set; }

    private ModelRuntimeState _state = ModelRuntimeState.NotInstalled;
    public ModelRuntimeState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StateChanged?.Invoke(this, _state);
            }
        }
    }
    public event EventHandler<ModelRuntimeState>? StateChanged;

    public bool IsNeuralModelLoaded => State == ModelRuntimeState.Ready || State == ModelRuntimeState.InferenceRunning;
    public string ActiveModelName { get; private set; } = string.Empty;
    public string StatusMessage { get; private set; } = "Chưa khởi động";
    public InferenceBackendType ActiveBackend { get; private set; } = InferenceBackendType.Cpu;
    public bool AllowLexicalFallback { get; set; } = false;

    public string ModelFingerprint { get; private set; } = "unloaded";
    public int BeamSize
    {
        get => _explicitBeamSize ?? 2;
        set => _explicitBeamSize = Math.Max(1, value);
    }
    public double LengthPenalty { get; set; } = 1.0;
    public double RepetitionPenalty { get; set; } = 1.05;
    public int NoRepeatNgramSize { get; set; } = 3;
    public int RequestTimeoutMs { get; set; } = 10000;
    public string DecodingConfig => $"beam={BeamSize},lp={LengthPenalty:0.##},rp={RepetitionPenalty:0.##},ng={NoRepeatNgramSize},v2-general";
    public string CacheIdentity => EnableMultiModelRouter
        ? $"LocalNeural|RouterV1(zhvi={EnableExperimentalZhViOpusModel},fb={AllowSpecializedFallbackToM2M100},max={MaxLoadedModelsInMemory})|fp={ModelFingerprint}|dec={DecodingConfig}"
        : $"LocalNeural|M2M-100-418M|fp={ModelFingerprint}|dec={DecodingConfig}";

    public string LastModelUsed { get; private set; } = string.Empty;
    public bool LastFallbackUsed { get; private set; }
    public double LastModelLoadMs { get; private set; }
    public double LastTokenizeMs { get; private set; }
    public double LastInferMs { get; private set; }
    public double LastPostprocMs { get; private set; }
    public long WorkerCrashOrResetCount => Interlocked.Read(ref _workerCrashOrResetCount);
    private long _workerCrashOrResetCount;
    private EventHandler? _processExitHandler;

    public LocalNeuralTranslationEngine(
        ISettingsService settingsService,
        string pythonExePath = "python",
        string workerScriptPath = "workers/translation/translation_worker.py")
        : this(pythonExePath, workerScriptPath)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        RefreshInitialStatus();
    }

    public LocalNeuralTranslationEngine(
        string pythonExePath = "python",
        string workerScriptPath = "workers/translation/translation_worker.py")
    {
        _pythonExePath = pythonExePath;
        _workerScriptPath = workerScriptPath;
        RefreshInitialStatus();
    }

    private void RefreshInitialStatus()
    {
        if (IsAnyTranslationModelInstalledOnDisk(CustomModelsRoot))
        {
            State = ModelRuntimeState.Loading;
            ActiveModelName = EnableMultiModelRouter
                ? "Offline Multi-Model Router (CT2 INT8)"
                : "M2M-100 418M (INT8 Neural)";
            StatusMessage = EnableMultiModelRouter
                ? "Sẵn sàng khởi động Multi-Model Router..."
                : "Đang nạp mô hình M2M-100...";
        }
        else
        {
            State = ModelRuntimeState.NotInstalled;
            ActiveModelName = string.Empty;
            StatusMessage = "Chưa cài đặt mô hình dịch Offline";
        }
    }

    public static string NormalizeLanguageCode(string? lang)
    {
        string c = (lang ?? "auto").Trim().ToLowerInvariant();
        return c switch
        {
            "zh" or "zh-cn" or "zh-hans" or "zh-tw" or "zh-hant" => "zh",
            "vi" or "vn" => "vi",
            "en" => "en",
            _ => "auto"
        };
    }

    public string ResolvePreferredModelId(string sourceLang, string targetLang)
    {
        if (!EnableMultiModelRouter) return ModelIdM2M100;
        string src = NormalizeLanguageCode(sourceLang);
        string tgt = NormalizeLanguageCode(targetLang);
        if (tgt == "auto") tgt = "vi";

        if ((src == "en" && tgt == "vi") || (src == "vi" && tgt == "en"))
        {
            return ModelIdEnViT5;
        }
        if (src == "zh" && tgt == "vi" && EnableExperimentalZhViOpusModel)
        {
            return ModelIdOpusZhVi;
        }
        return ModelIdM2M100;
    }

    public static string GetModelFamily(string modelId) => modelId switch
    {
        ModelIdEnViT5 => "envit5",
        ModelIdOpusZhVi => "opus_zh_vi",
        _ => "m2m100"
    };

    public static string GetModelDisplayName(string modelId) => modelId switch
    {
        ModelIdEnViT5 => "VietAI EnViT5 (CT2 INT8)",
        ModelIdOpusZhVi => "Opus-MT Zh-Vi (CT2 INT8)",
        _ => "M2M-100 418M (INT8 Neural)"
    };

    public int GetEffectiveBeamSize(string modelId, string? sourceLang = null, string? targetLang = null)
    {
        if (_explicitBeamSize.HasValue) return _explicitBeamSize.Value;
        if (modelId == ModelIdEnViT5)
        {
            return string.Equals(sourceLang, "vi", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }
        return 2;
    }

    public string GetDecodingConfigForModel(string modelId, string? sourceLang = null, string? targetLang = null) =>
        $"model={GetModelFamily(modelId)},beam={GetEffectiveBeamSize(modelId, sourceLang, targetLang)},lp={LengthPenalty:0.##},rp={RepetitionPenalty:0.##},ng={NoRepeatNgramSize},v2-general";

    public string ResolveEffectiveModelIdForCache(string sourceLang, string targetLang, out bool usedFallback)
    {
        string preferred = ResolvePreferredModelId(sourceLang, targetLang);
        usedFallback = false;

        if (!string.IsNullOrEmpty(FindInstalledModelDirectory(preferred, CustomModelsRoot)))
        {
            return preferred;
        }

        if (AllowSpecializedFallbackToM2M100 &&
            preferred != ModelIdM2M100 &&
            !string.IsNullOrEmpty(FindInstalledModelDirectory(ModelIdM2M100, CustomModelsRoot)))
        {
            usedFallback = true;
            return ModelIdM2M100;
        }

        return preferred;
    }

    public string GetCacheIdentity(string sourceLang, string targetLang)
    {
        if (!EnableMultiModelRouter)
        {
            return CacheIdentity;
        }

        string effModel = ResolveEffectiveModelIdForCache(sourceLang, targetLang, out bool fb);
        string fp = _knownFingerprints.TryGetValue(effModel, out var knownFp)
            ? knownFp
            : (effModel == ModelIdM2M100 ? ModelFingerprint : "unloaded");
        return $"LocalNeural|{effModel}|fp={fp}|fb={fb}|zhvi={EnableExperimentalZhViOpusModel}|dec={GetDecodingConfigForModel(effModel, sourceLang, targetLang)}";
    }

    public string BuildCacheIdentityForTargets(
        string detectedSourceLang,
        IReadOnlyList<TargetLanguage> targets,
        IReadOnlyDictionary<TargetLanguage, NeuralTranslationResponse>? actualResponses = null)
    {
        if (!EnableMultiModelRouter)
        {
            return CacheIdentity;
        }

        var parts = new List<string>(targets.Count);
        foreach (var target in targets.OrderBy(t => t))
        {
            string tgtCode = target switch
            {
                TargetLanguage.English => "en",
                TargetLanguage.Chinese => "zh",
                _ => "vi"
            };

            if (actualResponses != null && actualResponses.TryGetValue(target, out var resp))
            {
                parts.Add($"{tgtCode}:LocalNeural|{resp.ModelUsed}|fp={resp.ModelFingerprint}|fb={resp.FallbackUsed}|zhvi={EnableExperimentalZhViOpusModel}|dec={GetDecodingConfigForModel(resp.ModelUsed, detectedSourceLang, tgtCode)}");
            }
            else
            {
                parts.Add($"{tgtCode}:{GetCacheIdentity(detectedSourceLang, tgtCode)}");
            }
        }

        return string.Join(";", parts);
    }

    public static bool IsDefaultModelInstalledOnDisk()
    {
        return !string.IsNullOrEmpty(FindInstalledModelDirectory(ModelIdM2M100, null));
    }

    public static bool IsAnyTranslationModelInstalledOnDisk(string? customModelsRoot = null)
    {
        return !string.IsNullOrEmpty(FindInstalledModelDirectory(ModelIdM2M100, customModelsRoot)) ||
               !string.IsNullOrEmpty(FindInstalledModelDirectory(ModelIdEnViT5, customModelsRoot)) ||
               !string.IsNullOrEmpty(FindInstalledModelDirectory(ModelIdOpusZhVi, customModelsRoot));
    }

    public static string? FindInstalledModelDirectory()
    {
        return FindInstalledModelDirectory(ModelIdM2M100, null);
    }

    public static string? FindInstalledModelDirectory(string modelId, string? customModelsRoot = null)
    {
        try
        {
            string folder = modelId switch
            {
                ModelIdEnViT5 => "envit5_ct2_int8",
                ModelIdOpusZhVi => "opus_mt_zh_vi_ct2",
                _ => "m2m100_418m_ct2"
            };

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(customModelsRoot))
            {
                candidates.Add(Path.Combine(customModelsRoot, folder));
                candidates.Add(Path.Combine(customModelsRoot, "translation", folder));
            }
            else
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                candidates.Add(Path.Combine(localAppData, "ScreenSubAI", "Models", "translation", folder));
                candidates.Add(Path.Combine(localAppData, "ScreenSubAI", "Models", "translation_eval", folder));
                candidates.Add(Path.Combine(AppContext.BaseDirectory, "models", "translation", folder));
                candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "models", "translation", folder));
            }

            foreach (var dir in candidates)
            {
                if (IsValidModelDirectory(dir, modelId))
                {
                    return dir;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValidModelDirectory(string dir, string modelId)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        string modelBin = Path.Combine(dir, "model.bin");
        if (!File.Exists(modelBin) || new FileInfo(modelBin).Length <= 0) return false;

        return modelId switch
        {
            ModelIdEnViT5 => File.Exists(Path.Combine(dir, "tokenizer.json")),
            ModelIdOpusZhVi => File.Exists(Path.Combine(dir, "source.spm")) && File.Exists(Path.Combine(dir, "target.spm")),
            _ => File.Exists(Path.Combine(dir, "sentencepiece.bpe.model")) && new FileInfo(modelBin).Length > 100 * 1024 * 1024
        };
    }

    public Task InitializeAsync(string? modelPath, InferenceBackendType backend = InferenceBackendType.Cpu, CancellationToken ct = default)
    {
        ActiveBackend = backend;
        return InitializeAsync(ct);
    }

    /// <summary>
    /// Starts the Python translation worker process and checks for the M2M-100 neural model.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await InitializeUnlockedAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task InitializeUnlockedAsync(CancellationToken ct)
    {
        if (_workerProcess != null && !_workerProcess.HasExited && IsNeuralModelLoaded)
        {
            return;
        }

        ResetWorkerUnlocked(countAsFailure: false);
        StatusMessage = "Đang kết nối Neural Translation Worker...";

        string fullScriptPath = ResolveScriptPath();
        if (!File.Exists(fullScriptPath))
        {
            State = ModelRuntimeState.NotInstalled;
            ActiveModelName = "MODEL NOT INSTALLED";
            StatusMessage = $"Không tìm thấy translation worker: {fullScriptPath}";
            return;
        }

        string pythonExe = ResolvePythonPath(_pythonExePath);

        var argParts = new List<string> { $"\"{fullScriptPath}\"" };
        if (EnableMultiModelRouter)
        {
            argParts.Add("--router");
        }
        if (!EnableExperimentalZhViOpusModel)
        {
            argParts.Add("--no-experimental-zh-vi");
        }
        argParts.Add($"--max-loaded-models {MaxLoadedModelsInMemory}");
        if (!string.IsNullOrWhiteSpace(CustomModelsRoot))
        {
            argParts.Add($"--models-root \"{CustomModelsRoot}\"");
        }
        if (EnableMultiModelRouter || SkipStartupAutoLoad || !string.IsNullOrWhiteSpace(CustomModelsRoot))
        {
            argParts.Add("--lazy-load");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = string.Join(" ", argParts),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false),
            StandardInputEncoding = new System.Text.UTF8Encoding(false)
        };

        _workerProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var spawnedProc = _workerProcess;
        spawnedProc.Exited += (_, _) =>
        {
            if (!_isDisposed && ReferenceEquals(_workerProcess, spawnedProc))
            {
                State = ModelRuntimeState.Error;
                StatusMessage = "Tiến trình Neural Translation worker đã thoát.";
            }
        };

        try
        {
            spawnedProc.Start();
            // Drain stderr continuously so pipe buffer cannot stall worker
            spawnedProc.ErrorDataReceived += (_, _) => { };
            spawnedProc.BeginErrorReadLine();

            _processExitHandler ??= (_, _) =>
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
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
            AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        }
        catch (Exception ex)
        {
            State = ModelRuntimeState.Error;
            ActiveModelName = "MODEL NOT INSTALLED";
            StatusMessage = $"Không thể khởi chạy Python: {ex.Message}";
            ResetWorkerUnlocked(countAsFailure: true);
            return;
        }

        _stdin = spawnedProc.StandardInput;
        _stdout = spawnedProc.StandardOutput;

        try
        {
            // Ping worker with timeout (allow up to 30s for Python worker startup & model auto-load)
            int pingId = Interlocked.Increment(ref _requestIdCounter);
            using var pingJson = await SendAndReceiveJsonUnlockedAsync(new { id = pingId, cmd = "ping" }, pingId, timeoutMs: 30000, ct);
            var root = pingJson.RootElement;
            bool modelLoaded = root.TryGetProperty("model_loaded", out var mlProp) && mlProp.GetBoolean();
            string modelName = root.TryGetProperty("model_name", out var mnProp) ? mnProp.GetString() ?? "MODEL NOT INSTALLED" : "MODEL NOT INSTALLED";
            if (root.TryGetProperty("model_fingerprint", out var fpProp) && !string.IsNullOrWhiteSpace(fpProp.GetString()))
            {
                ModelFingerprint = fpProp.GetString()!;
            }

            bool anyInstalledInWorker = false;
            if (root.TryGetProperty("registry", out var regProp) && regProp.ValueKind == JsonValueKind.Object)
            {
                foreach (var entryProp in regProp.EnumerateObject())
                {
                    bool inst = entryProp.Value.TryGetProperty("installed", out var iProp) && iProp.GetBoolean();
                    if (inst) anyInstalledInWorker = true;
                    if (entryProp.Value.TryGetProperty("model_fingerprint", out var entryFpProp))
                    {
                        string? efp = entryFpProp.GetString();
                        if (!string.IsNullOrWhiteSpace(efp))
                        {
                            _knownFingerprints[entryProp.Name] = efp!;
                        }
                    }
                }
            }

            if (modelLoaded)
            {
                State = ModelRuntimeState.Ready;
                ActiveModelName = EnableMultiModelRouter ? "Offline Multi-Model Router (CT2 INT8)" : "M2M-100 418M (INT8 Neural)";
                StatusMessage = $"Sẵn sàng ({modelName})";
                return;
            }

            if (EnableMultiModelRouter && (anyInstalledInWorker || IsAnyTranslationModelInstalledOnDisk(CustomModelsRoot)))
            {
                // Push explicit fallback/experimental config into worker before lazy-loading on first request
                int cfgId = Interlocked.Increment(ref _requestIdCounter);
                using var cfgDoc = await SendAndReceiveJsonUnlockedAsync(
                    new
                    {
                        id = cfgId,
                        cmd = "configure_router",
                        enabled = EnableMultiModelRouter,
                        experimental_zh_vi = EnableExperimentalZhViOpusModel,
                        allow_fallback = AllowSpecializedFallbackToM2M100,
                        max_loaded_models = MaxLoadedModelsInMemory,
                        models_root = CustomModelsRoot ?? string.Empty
                    },
                    cfgId,
                    timeoutMs: 15000,
                    ct);
                State = ModelRuntimeState.Ready;
                ActiveModelName = "Offline Multi-Model Router (CT2 INT8)";
                StatusMessage = "Sẵn sàng (Multi-Model Router — Lazy Load)";
                return;
            }

            string? installedDir = FindInstalledModelDirectory(ModelIdM2M100, CustomModelsRoot);
            if (!string.IsNullOrEmpty(installedDir))
            {
                int loadId = Interlocked.Increment(ref _requestIdCounter);
                using var loadDoc = await SendAndReceiveJsonUnlockedAsync(
                    new
                    {
                        id = loadId,
                        cmd = "load_model",
                        model_dir = installedDir,
                        device = "cpu",
                        threads = 4
                    },
                    loadId,
                    timeoutMs: 60000,
                    ct);

                if (loadDoc.RootElement.TryGetProperty("status", out var loadStatus) && loadStatus.GetString() == "ok")
                {
                    if (loadDoc.RootElement.TryGetProperty("model_fingerprint", out var lfp) && !string.IsNullOrWhiteSpace(lfp.GetString()))
                    {
                        ModelFingerprint = lfp.GetString()!;
                        _knownFingerprints[ModelIdM2M100] = ModelFingerprint;
                    }
                    State = ModelRuntimeState.Ready;
                    ActiveModelName = "M2M-100 418M (INT8 Neural)";
                    StatusMessage = "Sẵn sàng (M2M-100 Neural INT8)";
                    return;
                }
            }

            if (IsAnyTranslationModelInstalledOnDisk(CustomModelsRoot))
            {
                State = ModelRuntimeState.Ready;
                ActiveModelName = EnableMultiModelRouter ? "Offline Multi-Model Router (CT2 INT8)" : "M2M-100 418M (INT8 Neural)";
                StatusMessage = EnableMultiModelRouter ? "Sẵn sàng (Multi-Model Router)" : "Sẵn sàng (M2M-100 Neural INT8)";
            }
            else
            {
                State = ModelRuntimeState.NotInstalled;
                ActiveModelName = string.Empty;
                StatusMessage = "Chưa cài đặt mô hình dịch Offline";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ResetWorkerUnlocked(countAsFailure: true);
            State = ModelRuntimeState.Error;
            ActiveModelName = string.Empty;
            StatusMessage = $"Lỗi khởi tạo Neural Engine: {ex.Message}";
        }
    }

    private void ResetWorkerUnlocked(bool countAsFailure)
    {
        if (countAsFailure)
        {
            Interlocked.Increment(ref _workerCrashOrResetCount);
        }

        try { _stdin?.Dispose(); } catch { }
        try { _stdout?.Dispose(); } catch { }
        if (_workerProcess != null)
        {
            try
            {
                if (!_workerProcess.HasExited)
                {
                    _workerProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
            try { _workerProcess.Dispose(); } catch { }
        }
        _stdin = null;
        _stdout = null;
        _workerProcess = null;
    }

    /// <summary>
    /// Serialized, stream-safe JSON-lines IPC request/response cycle under _lock.
    /// Once a request line is written to stdin, waits for the matching reqId response up to timeoutMs
    /// before checking caller cancellation, preventing orphaned unread lines from desynchronizing stdout.
    /// </summary>
    private async Task<JsonDocument> SendAndReceiveJsonUnlockedAsync(object reqObj, int reqId, int timeoutMs, CancellationToken callerCt)
    {
        if (_stdin == null || _stdout == null || _workerProcess == null || _workerProcess.HasExited)
        {
            throw new IOException("Neural worker process is not running.");
        }

        callerCt.ThrowIfCancellationRequested();

        string jsonReq = JsonSerializer.Serialize(reqObj);
        await _stdin.WriteLineAsync(jsonReq.AsMemory(), callerCt);
        await _stdin.FlushAsync(callerCt);

        using var ioTimeoutCts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            string? respLine;
            try
            {
                respLine = await _stdout.ReadLineAsync(ioTimeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                ResetWorkerUnlocked(countAsFailure: true);
                throw new TimeoutException($"Neural worker IPC timed out after {timeoutMs}ms for reqId={reqId}.");
            }

            if (respLine == null)
            {
                ResetWorkerUnlocked(countAsFailure: true);
                throw new EndOfStreamException($"Neural worker terminated unexpectedly (EOF) for reqId={reqId}.");
            }

            if (string.IsNullOrWhiteSpace(respLine))
            {
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(respLine);
            }
            catch (JsonException jex)
            {
                ResetWorkerUnlocked(countAsFailure: true);
                throw new InvalidDataException($"Malformed JSON from neural worker for reqId={reqId}: {jex.Message}");
            }

            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out int respId))
            {
                if (respId < reqId)
                {
                    // Discard leftover response from an older request ID
                    doc.Dispose();
                    continue;
                }
                if (respId != reqId)
                {
                    doc.Dispose();
                    ResetWorkerUnlocked(countAsFailure: true);
                    throw new InvalidDataException($"Worker response ID mismatch: expected {reqId}, got {respId}.");
                }
            }

            // Response matched and stdout is synchronized! Now honor caller cancellation cleanly.
            if (callerCt.IsCancellationRequested)
            {
                doc.Dispose();
                callerCt.ThrowIfCancellationRequested();
            }

            return doc;
        }
    }

    /// <summary>
    /// Translates text using the active offline neural router (or M2M-100 baseline).
    /// Returns only translated text; use TranslateTextDetailedAsync for routing/telemetry metadata.
    /// </summary>
    public async Task<string> TranslateTextAsync(string text, string sourceLang, string targetLang, CancellationToken ct = default)
    {
        var detailed = await TranslateTextDetailedAsync(text, sourceLang, targetLang, ct);
        return detailed.TranslatedText;
    }

    /// <summary>
    /// Translates text using the configured offline neural router or M2M-100 baseline and returns full model telemetry.
    /// Never silently falls back to Mock or Cloud on IPC/model failure; recovers dead workers once before surfacing error.
    /// </summary>
    public async Task<NeuralTranslationResponse> TranslateTextDetailedAsync(
        string text,
        string sourceLang,
        string targetLang,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new NeuralTranslationResponse(
                string.Empty, string.Empty, string.Empty, string.Empty, ModelFingerprint, false, DecodingConfig, 0, 0, 0, 0, 0);
        }

        string cleanText = text.Trim();
        var swTotal = Stopwatch.StartNew();

        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            await _lock.WaitAsync(ct);
            bool needRetryAfterCrash = false;
            try
            {
                if (!IsNeuralModelLoaded || _workerProcess == null || _workerProcess.HasExited || _stdin == null || _stdout == null)
                {
                    await InitializeUnlockedAsync(ct);
                }

                if (!IsNeuralModelLoaded || _stdin == null || _stdout == null)
                {
                    break;
                }

                State = ModelRuntimeState.InferenceRunning;
                int reqId = Interlocked.Increment(ref _requestIdCounter);
                string expectedRouteModel = ResolveEffectiveModelIdForCache(sourceLang, targetLang, out _);
                int effectiveBeam = GetEffectiveBeamSize(expectedRouteModel, sourceLang, targetLang);

                var reqObj = new
                {
                    id = reqId,
                    cmd = "translate",
                    text = cleanText,
                    source_lang = sourceLang,
                    target_lang = targetLang,
                    beam_size = effectiveBeam,
                    length_penalty = LengthPenalty,
                    repetition_penalty = RepetitionPenalty,
                    no_repeat_ngram_size = NoRepeatNgramSize,
                    routing_enabled = EnableMultiModelRouter,
                    experimental_zh_vi = EnableExperimentalZhViOpusModel,
                    allow_fallback = AllowSpecializedFallbackToM2M100,
                    max_loaded_models = MaxLoadedModelsInMemory,
                    models_root = CustomModelsRoot
                };

                using var doc = await SendAndReceiveJsonUnlockedAsync(reqObj, reqId, RequestTimeoutMs, ct);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var st) && st.GetString() == "ok")
                {
                    if (root.TryGetProperty("tokenize_ms", out var tMs)) LastTokenizeMs = tMs.GetDouble();
                    if (root.TryGetProperty("infer_ms", out var iMs)) LastInferMs = iMs.GetDouble();
                    if (root.TryGetProperty("postproc_ms", out var pMs)) LastPostprocMs = pMs.GetDouble();
                    double modelLoadMs = root.TryGetProperty("model_load_ms", out var lMs) ? lMs.GetDouble() : 0.0;
                    LastModelLoadMs = modelLoadMs;

                    string modelUsed = root.TryGetProperty("model_used", out var muProp) && !string.IsNullOrWhiteSpace(muProp.GetString())
                        ? muProp.GetString()!
                        : ModelIdM2M100;
                    string modelName = root.TryGetProperty("model_name", out var mnProp) && !string.IsNullOrWhiteSpace(mnProp.GetString())
                        ? mnProp.GetString()!
                        : GetModelDisplayName(modelUsed);
                    string modelFp = root.TryGetProperty("model_fingerprint", out var mfpProp) && !string.IsNullOrWhiteSpace(mfpProp.GetString())
                        ? mfpProp.GetString()!
                        : ModelFingerprint;
                    bool fallbackUsed = root.TryGetProperty("fallback_used", out var fbProp) && fbProp.GetBoolean();
                    string decCfg = root.TryGetProperty("decoding_config", out var decProp) && !string.IsNullOrWhiteSpace(decProp.GetString())
                        ? decProp.GetString()!
                        : GetDecodingConfigForModel(modelUsed);

                    _knownFingerprints[modelUsed] = modelFp;
                    if (!EnableMultiModelRouter || modelUsed == ModelIdM2M100)
                    {
                        ModelFingerprint = modelFp;
                    }
                    LastModelUsed = modelUsed;
                    LastFallbackUsed = fallbackUsed;
                    ActiveModelName = fallbackUsed ? $"{modelName} (Fallback)" : modelName;
                    StatusMessage = $"Sẵn sàng ({ActiveModelName})";

                    string trans = root.GetProperty("translated_text").GetString() ?? string.Empty;
                    string rawTrans = root.TryGetProperty("raw_translated_text", out var rawProp) ? rawProp.GetString() ?? trans : trans;
                    State = ModelRuntimeState.Ready;
                    if (!string.IsNullOrWhiteSpace(trans))
                    {
                        swTotal.Stop();
                        return new NeuralTranslationResponse(
                            trans,
                            rawTrans,
                            modelUsed,
                            modelName,
                            modelFp,
                            fallbackUsed,
                            decCfg,
                            modelLoadMs,
                            LastTokenizeMs,
                            LastInferMs,
                            LastPostprocMs,
                            swTotal.ElapsedMilliseconds);
                    }
                    throw new InvalidOperationException("Neural worker returned empty translation.");
                }

                string errMsg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Unknown worker error" : "Unknown worker error";
                throw new InvalidOperationException($"Neural worker returned error: {errMsg}");
            }
            catch (OperationCanceledException)
            {
                if (IsNeuralModelLoaded)
                {
                    State = ModelRuntimeState.Ready;
                }
                throw;
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or TimeoutException or InvalidDataException)
            {
                State = ModelRuntimeState.Error;
                StatusMessage = $"Lỗi IPC Neural Worker (lần {attempt + 1}): {ex.Message}";
                ResetWorkerUnlocked(countAsFailure: attempt == 0 ? false : true);
                if (attempt == 0 && !ct.IsCancellationRequested)
                {
                    needRetryAfterCrash = true;
                }
                else
                {
                    throw new InvalidOperationException(StatusMessage, ex);
                }
            }
            catch (InvalidOperationException)
            {
                State = ModelRuntimeState.Ready;
                throw;
            }
            catch (Exception ex)
            {
                State = ModelRuntimeState.Error;
                StatusMessage = $"Lỗi suy luận Neural: {ex.Message}";
                throw;
            }
            finally
            {
                if (State == ModelRuntimeState.InferenceRunning)
                {
                    State = ModelRuntimeState.Ready;
                }
                _lock.Release();
            }

            if (needRetryAfterCrash)
            {
                continue;
            }
        }

        // 3. Explicit Fallback Handling when model is not installed on disk
        if (AllowLexicalFallback)
        {
            string lexicalResult = TranslateLexical(cleanText, sourceLang, targetLang);
            string tagged = $"[LEXICAL — LIMITED VOCABULARY] {lexicalResult}";
            return new NeuralTranslationResponse(tagged, lexicalResult, "lexical", "Lexical Dictionary", "lexical", true, "lexical", 0, 0, 0, 0, swTotal.ElapsedMilliseconds);
        }

        string notInstalled = $"[MODEL NOT INSTALLED: Vui lòng tải mô hình Neural trong Cài đặt] {cleanText}";
        return new NeuralTranslationResponse(notInstalled, cleanText, "none", "MODEL NOT INSTALLED", "none", false, DecodingConfig, 0, 0, 0, 0, swTotal.ElapsedMilliseconds);
    }

    public async Task<JsonDocument> GetWorkerRegistryAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_workerProcess == null || _workerProcess.HasExited || _stdin == null || _stdout == null)
            {
                await InitializeUnlockedAsync(ct);
            }
            int reqId = Interlocked.Increment(ref _requestIdCounter);
            return await SendAndReceiveJsonUnlockedAsync(new { id = reqId, cmd = "get_registry", models_root = CustomModelsRoot }, reqId, 10000, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JsonDocument> ConfigureWorkerRouterAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_workerProcess == null || _workerProcess.HasExited || _stdin == null || _stdout == null)
            {
                await InitializeUnlockedAsync(ct);
            }
            int reqId = Interlocked.Increment(ref _requestIdCounter);
            return await SendAndReceiveJsonUnlockedAsync(
                new
                {
                    id = reqId,
                    cmd = "configure_router",
                    enabled = EnableMultiModelRouter,
                    experimental_zh_vi = EnableExperimentalZhViOpusModel,
                    allow_fallback = AllowSpecializedFallbackToM2M100,
                    max_loaded_models = MaxLoadedModelsInMemory,
                    models_root = CustomModelsRoot ?? string.Empty
                },
                reqId,
                15000,
                ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Heuristic phrase and dictionary lookup (kept for benchmark comparison and explicit lexical mode).
    /// </summary>
    public static string TranslateLexical(string text, string sourceLang, string targetLang)
    {
        sourceLang = sourceLang.ToLowerInvariant();
        targetLang = targetLang.ToLowerInvariant();

        if (sourceLang is "auto" or "")
        {
            if (text.Any(c => c >= 0x4E00 && c <= 0x9FFF))
                sourceLang = "zh";
            else if (Regex.IsMatch(text, @"[àáạảãâầấậẩẫăằắặẳẵèéẹẻẽêềếệểễìíịỉĩòóọỏõôồốộổỗơờớợởỡùúụủũưừứựửữỳýỵỷỹđ]", RegexOptions.IgnoreCase))
                sourceLang = "vi";
            else
                sourceLang = "en";
        }

        string cleanText = text.Trim();

        // Check phrase dictionary
        if (PhraseDictionary.TryGetValue(cleanText, out var phrase))
        {
            return targetLang switch
            {
                "en" => phrase.En,
                "zh" or "zh-hans" => phrase.Zh,
                "zh-hant" => LocalChineseConverter.ToTraditional(phrase.Zh),
                "vi" => phrase.Vi,
                _ => phrase.Vi
            };
        }

        string simplified = LocalChineseConverter.ToSimplified(cleanText);
        if (PhraseDictionary.TryGetValue(simplified, out var simpPhrase))
        {
            return targetLang switch
            {
                "en" => simpPhrase.En,
                "zh" or "zh-hans" => simpPhrase.Zh,
                "zh-hant" => LocalChineseConverter.ToTraditional(simpPhrase.Zh),
                "vi" => simpPhrase.Vi,
                _ => simpPhrase.Vi
            };
        }

        string workingText = cleanText;
        foreach (var kv in PhraseDictionary.OrderByDescending(p => p.Key.Length))
        {
            if (kv.Key.Length >= 2 && workingText.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string repl = targetLang switch
                {
                    "en" => kv.Value.En,
                    "zh" or "zh-hans" => kv.Value.Zh,
                    "zh-hant" => LocalChineseConverter.ToTraditional(kv.Value.Zh),
                    "vi" => kv.Value.Vi,
                    _ => kv.Value.Vi
                };
                workingText = Regex.Replace(workingText, Regex.Escape(kv.Key), repl, RegexOptions.IgnoreCase);
            }
        }

        var words = Regex.Split(workingText, @"(\s+|[.,!?:;，。！？；])");
        var translatedTokens = new List<string>();

        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w) || Regex.IsMatch(w, @"^[.,!?:;，。！？；]+$"))
            {
                translatedTokens.Add(w);
                continue;
            }

            string matched = w;
            foreach (var kv in Vocabulary)
            {
                var entry = kv.Value;
                bool hit = sourceLang switch
                {
                    "en" => w.Equals(entry.En, StringComparison.OrdinalIgnoreCase),
                    "zh" => w.Contains(entry.Zh),
                    "vi" => w.Equals(entry.Vi, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };

                if (hit)
                {
                    matched = targetLang switch
                    {
                        "en" => entry.En,
                        "zh" => entry.Zh,
                        "vi" => entry.Vi,
                        _ => entry.En
                    };
                    break;
                }
            }
            translatedTokens.Add(matched);
        }

        string result = string.Join("", translatedTokens);
        if (targetLang == "zh-hant")
        {
            result = LocalChineseConverter.ToTraditional(result);
        }

        return result;
    }

    private string ResolveScriptPath()
    {
        string inBase = Path.Combine(AppContext.BaseDirectory, _workerScriptPath);
        if (File.Exists(inBase)) return inBase;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            string candidate = Path.Combine(dir.FullName, _workerScriptPath);
            if (File.Exists(candidate)) return candidate;

            string altCandidate = Path.Combine(dir.FullName, "workers", "translation", "translation_worker.py");
            if (File.Exists(altCandidate)) return altCandidate;

            dir = dir.Parent;
        }

        string inCwd = Path.Combine(Directory.GetCurrentDirectory(), _workerScriptPath);
        if (File.Exists(inCwd)) return inCwd;

        return inBase;
    }

    private static string ResolvePythonPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
            return configuredPath;

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

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            var paths = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in paths)
            {
                if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = Path.Combine(dir, "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return configuredPath;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            if (_stdin != null)
            {
                var quitReq = JsonSerializer.Serialize(new { cmd = "quit" });
                _stdin.WriteLine(quitReq);
                _stdin.Flush();
            }

            if (_workerProcess != null && !_workerProcess.HasExited)
            {
                if (!_workerProcess.WaitForExit(1000))
                {
                    _workerProcess.Kill();
                }
                _workerProcess.Dispose();
            }
        }
        catch { }

        _lock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask;
    }

    private static readonly Dictionary<string, (string En, string Zh, string Vi)> PhraseDictionary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["你今天有时间吗？"] = ("Are you free today?", "你今天有时间吗？", "Hôm nay bạn có rảnh không?"),
        ["你今天有時間嗎？"] = ("Are you free today?", "你今天有時間嗎？", "Hôm nay bạn có rảnh không?"),
        ["你今天有空吗？"] = ("Are you free today?", "你今天有空吗？", "Hôm nay bạn có rảnh không?"),
        ["Are you free today?"] = ("Are you free today?", "你今天有时间吗？", "Hôm nay bạn có rảnh không?"),
        ["Hôm nay bạn có rảnh không?"] = ("Are you free today?", "你今天有时间吗？", "Hôm nay bạn có rảnh không?"),
        ["你好"] = ("Hello", "你好", "Xin chào"),
        ["Hello"] = ("Hello", "你好", "Xin chào"),
        ["Xin chào"] = ("Hello", "你好", "Xin chào"),
        ["谢谢"] = ("Thank you", "谢谢", "Cảm ơn"),
        ["謝謝"] = ("Thank you", "謝謝", "Cảm ơn"),
        ["Thank you"] = ("Thank you", "谢谢", "Cảm ơn"),
        ["Cảm ơn"] = ("Thank you", "谢谢", "Cảm ơn"),
        ["cảm ơn bạn"] = ("Thank you", "谢谢你", "Cảm ơn bạn"),
        ["cảm ơn"] = ("Thank you", "谢谢", "Cảm ơn"),
        ["xin chào"] = ("Hello", "你好", "Xin chào"),
        ["对不起"] = ("I'm sorry", "对不起", "Xin lỗi"),
        ["對不起"] = ("I'm sorry", "對不起", "Xin lỗi"),
        ["I'm sorry"] = ("I'm sorry", "对不起", "Xin lỗi"),
        ["Xin lỗi"] = ("I'm sorry", "对不起", "Xin lỗi"),
        ["再见"] = ("Goodbye", "再见", "Tạm biệt"),
        ["再見"] = ("Goodbye", "再見", "Tạm biệt"),
        ["Goodbye"] = ("Goodbye", "再见", "Tạm biệt"),
        ["Tạm biệt"] = ("Goodbye", "再见", "Tạm biệt"),
        ["你在做什么？"] = ("What are you doing?", "你在做什么？", "Bạn đang làm gì vậy?"),
        ["What are you doing?"] = ("What are you doing?", "你在做什么？", "Bạn đang làm gì vậy?"),
        ["Bạn đang làm gì vậy?"] = ("What are you doing?", "你在做什么？", "Bạn đang làm gì vậy?"),
        ["我明白了"] = ("I understand", "我明白了", "Tôi hiểu rồi"),
        ["I understand"] = ("I understand", "我明白了", "Tôi hiểu rồi"),
        ["Tôi hiểu rồi"] = ("I understand", "我明白了", "Tôi hiểu rồi"),
        ["没关系"] = ("It doesn't matter", "没关系", "Không sao đâu"),
        ["救命！"] = ("Help!", "救命！", "Cứu với!"),
        ["我们走吧"] = ("Let's go", "我们走吧", "Chúng ta đi thôi"),
        ["我喜欢你"] = ("I like you", "我喜欢你", "Tôi thích bạn"),
        ["我爱你"] = ("I love you", "我爱你", "Tôi yêu bạn"),
        ["快点！"] = ("Hurry up!", "快点！", "Nhanh lên!"),
        ["小心！"] = ("Be careful!", "小心！", "Cẩn thận!")
    };

    private static readonly Dictionary<string, (string En, string Zh, string Vi)> Vocabulary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["today"] = ("today", "今天", "hôm nay"),
        ["you"] = ("you", "你", "bạn"),
        ["free"] = ("free", "有空", "rảnh"),
        ["hello"] = ("hello", "你好", "xin chào"),
        ["thanks"] = ("thanks", "谢谢", "cảm ơn"),
        ["sorry"] = ("sorry", "对不起", "xin lỗi"),
        ["good"] = ("good", "好", "tốt"),
        ["weather"] = ("weather", "天气", "thời tiết"),
        ["cold"] = ("cold", "冷", "lạnh"),
        ["hot"] = ("hot", "热", "nóng"),
        ["love"] = ("love", "爱", "yêu"),
        ["like"] = ("like", "喜欢", "thích"),
        ["help"] = ("help", "帮忙", "giúp đỡ"),
        ["time"] = ("time", "时间", "thời gian"),
        ["go"] = ("go", "走", "đi"),
        ["come"] = ("come", "来", "đến")
    };
}
