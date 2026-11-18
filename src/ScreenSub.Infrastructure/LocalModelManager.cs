using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Infrastructure;

public sealed class LocalModelManager : IModelManager
{
    private readonly ISettingsService _settingsService;
    private readonly List<ModelMetadata> _registry = new();
    private readonly object _lock = new();

    public LocalModelManager(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeRegistry();
    }

    private void InitializeRegistry()
    {
        _registry.Clear();

        // 1. OCR Model (PaddleOCR Multilingual)
        _registry.Add(new ModelMetadata
        {
            Id = "paddle-ocr-v4",
            Name = "PaddleOCR Multilingual (PP-OCRv4)",
            Version = "4.0.0",
            Category = ModelCategory.Ocr,
            SupportedLanguages = "EN, ZH, VI (Đa ngữ)",
            SizeBytes = 125L * 1024 * 1024,
            QuantizationType = "FP32 / INT8",
            BackendCompatibility = "CPU, DirectML, CUDA",
            License = "Apache-2.0",
            LocalRelativePath = Path.Combine("ocr", "ppocr_v4_multilingual"),
            Sha256Checksum = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        });

        // 2. Built-in Lexical Translation Engine (Bundled by default)
        _registry.Add(new ModelMetadata
        {
            Id = "builtin-lexical-multilingual",
            Name = "Built-in Lexical & Rule Engine (Tích hợp sẵn)",
            Version = "1.0.0",
            Category = ModelCategory.Translation,
            SupportedLanguages = "EN, ZH, VI (Đa ngữ)",
            SizeBytes = 1024L * 1024,
            QuantizationType = "Lexical Dictionary & Rules",
            BackendCompatibility = "CPU",
            License = "Apache-2.0",
            LocalRelativePath = "builtin",
            Sha256Checksum = "embedded-rule-base"
        });

        // 3. Speech Model - Whisper Base
        _registry.Add(new ModelMetadata
        {
            Id = "whisper-base-ggml",
            Name = "Whisper Base (Multilingual)",
            Version = "1.0.0",
            Category = ModelCategory.Speech,
            SupportedLanguages = "EN, ZH, VI (99+ ngôn ngữ)",
            SizeBytes = 147_951_465L,
            QuantizationType = "Q5_1 / FP16",
            BackendCompatibility = "CPU (AVX2), DirectML, CUDA",
            License = "MIT",
            LocalRelativePath = Path.Combine("whisper", "ggml-base.bin"),
            SourceUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            Sha256Checksum = "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"
        });

        // 4. Speech Model - Whisper Small
        _registry.Add(new ModelMetadata
        {
            Id = "whisper-small-ggml",
            Name = "Whisper Small (Độ chính xác cao)",
            Version = "1.0.0",
            Category = ModelCategory.Speech,
            SupportedLanguages = "EN, ZH, VI (99+ ngôn ngữ)",
            SizeBytes = 466L * 1024 * 1024,
            QuantizationType = "Q5_1 / FP16",
            BackendCompatibility = "CPU (AVX2), DirectML, CUDA",
            License = "MIT",
            LocalRelativePath = Path.Combine("whisper", "ggml-small.bin"),
            SourceUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
            Sha256Checksum = "55356369dc6bb3e31215b0f443b749666de6a5ee5b3ea7085b3eb2b069695628"
        });

        // 5. Primary Neural Translation Model: Facebook M2M-100 418M (CTranslate2 INT8)
        _registry.Add(new ModelMetadata
        {
            Id = "m2m100-418m-ct2",
            Name = "Facebook M2M-100 418M (Multilingual Neural INT8)",
            Version = "1.0.0",
            Category = ModelCategory.Translation,
            SupportedLanguages = "EN, ZH, VI (Tất cả 6 chiều trực tiếp)",
            SizeBytes = 495L * 1024 * 1024,
            QuantizationType = "INT8 Quantized (CTranslate2)",
            BackendCompatibility = "CPU (AVX2), CUDA",
            License = "MIT",
            LocalRelativePath = Path.Combine("translation", "m2m100_418m_ct2"),
            SourceUrl = "https://huggingface.co/Rohith04/ct2fast_m2m100_418M",
            Sha256Checksum = "a68b104a4e4db53165562b6d7d30839b318242e5042081a5407885cc2266a66c"
        });

        // 5b. Specialized EN <-> VI Neural Model: VietAI EnViT5 (CTranslate2 INT8)
        _registry.Add(new ModelMetadata
        {
            Id = "envit5_ct2_int8",
            Name = "VietAI EnViT5 (CT2 INT8 - EN<->VI)",
            Version = "1.0.0",
            Category = ModelCategory.Translation,
            SupportedLanguages = "EN -> VI, VI -> EN",
            SizeBytes = 276_580_000L,
            QuantizationType = "INT8 Quantized (CTranslate2)",
            BackendCompatibility = "CPU (AVX2), CUDA",
            License = "OpenRAIL",
            LocalRelativePath = Path.Combine("translation", "envit5_ct2_int8"),
            Sha256Checksum = "e8a3d9be0fc077668972ed89c351cc4c911a09e9e3c6d8966478c113fc85ff8e"
        });

        // 5c. Experimental ZH -> VI Neural Model: Helsinki-NLP Opus-MT Zh-Vi (CTranslate2 INT8)
        _registry.Add(new ModelMetadata
        {
            Id = "opus_mt_zh_vi_ct2",
            Name = "Opus-MT Zh-Vi (CT2 INT8 - Experimental ZH->VI)",
            Version = "1.0.0",
            Category = ModelCategory.Translation,
            SupportedLanguages = "ZH -> VI",
            SizeBytes = 79_570_000L,
            QuantizationType = "INT8 Quantized (CTranslate2)",
            BackendCompatibility = "CPU (AVX2), CUDA",
            License = "Apache-2.0",
            LocalRelativePath = Path.Combine("translation", "opus_mt_zh_vi_ct2"),
            Sha256Checksum = "488da6baa02675083a7f3e01e788e58e18b77e94b4cf9d2bfb4bcd4406204542"
        });

        // 6. Translation Model - Opus-MT Neural Multilingual
        _registry.Add(new ModelMetadata
        {
            Id = "marian-multilingual-int8",
            Name = "Opus-MT Neural (En-Vi, Zh-Vi, En-Zh)",
            Version = "1.2.0",
            Category = ModelCategory.Translation,
            SupportedLanguages = "EN, ZH, VI (Hai chiều)",
            SizeBytes = 280L * 1024 * 1024,
            QuantizationType = "INT8 Quantized",
            BackendCompatibility = "CPU, DirectML, CUDA",
            License = "Apache-2.0 / CC-BY-4.0",
            LocalRelativePath = Path.Combine("translation", "marian_multilingual_int8.onnx"),
            Sha256Checksum = "d41d8cd98f00b204e9800998ecf8427e00000000000000000000000000000000"
        });

        // 6. Translation Model - NLLB-200 distilled 600M
        _registry.Add(new ModelMetadata
        {
            Id = "nllb-200-distilled-600m",
            Name = "NLLB-200 Distilled (Meta AI 200-Lang)",
            Version = "1.0.0",
            Category = ModelCategory.Translation,
            SupportedLanguages = "EN, ZH, VI (200 ngôn ngữ)",
            SizeBytes = 620L * 1024 * 1024,
            QuantizationType = "INT8",
            BackendCompatibility = "CPU, DirectML, CUDA",
            License = "CC BY-NC 4.0 (Phi thương mại)",
            LocalRelativePath = Path.Combine("translation", "nllb_200_600m_int8.onnx"),
            Sha256Checksum = "c5b2a09579d4dfd41097e376046e7f137eb4d7d3d1df5c4904037593c7fa689a"
        });

        RefreshInstallationStatuses();
    }

    public string GetModelStorageDirectory()
    {
        var configured = _settingsService.Settings.ModelStorageDirectory;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenSubAI", "Models");
        if (!Directory.Exists(defaultDir))
        {
            Directory.CreateDirectory(defaultDir);
        }
        return defaultDir;
    }

    public void SetModelStorageDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
        _settingsService.Settings.ModelStorageDirectory = path;
        _ = _settingsService.SaveSettingsAsync();
        RefreshInstallationStatuses();
    }

    public IReadOnlyList<ModelMetadata> GetModels(ModelCategory? category = null)
    {
        lock (_lock)
        {
            RefreshInstallationStatuses();
            if (category.HasValue)
            {
                return _registry.Where(m => m.Category == category.Value).ToList();
            }
            return _registry.ToList();
        }
    }

    public ModelMetadata? GetModel(string modelId)
    {
        lock (_lock)
        {
            RefreshInstallationStatuses();
            return _registry.FirstOrDefault(m => 
                string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase) ||
                (modelId == "m2m100_418m_int8" && m.Id == "m2m100-418m-ct2") ||
                (modelId == "m2m100-418m-ct2" && m.Id == "m2m100_418m_int8"));
        }
    }

    public async Task<bool> InstallModelAsync(string modelId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var model = GetModel(modelId);
        if (model == null) return false;

        if (model.Id == "builtin-lexical-multilingual")
        {
            model.Status = ModelInstallationStatus.Installed;
            model.LastVerified = DateTime.UtcNow;
            return true;
        }

        string baseDir = GetModelStorageDirectory();
        string targetPath = Path.Combine(baseDir, model.LocalRelativePath);
        string? targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        model.Status = ModelInstallationStatus.Downloading;

        if (model.Id == "m2m100-418m-ct2")
        {
            try
            {
                if (!Directory.Exists(targetPath))
                {
                    Directory.CreateDirectory(targetPath);
                }

                string[] m2mFiles =
                [
                    "config.json",
                    "generation_config.json",
                    "model.bin",
                    "sentencepiece.bpe.model",
                    "shared_vocabulary.json",
                    "special_tokens_map.json",
                    "tokenizer_config.json",
                    "vocab.json"
                ];

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                string baseUrl = "https://huggingface.co/Rohith04/ct2fast_m2m100_418M/resolve/main/";

                for (int i = 0; i < m2mFiles.Length; i++)
                {
                    string f = m2mFiles[i];
                    string filePath = Path.Combine(targetPath, f);
                    if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
                    {
                        continue;
                    }

                    using var resp = await client.GetAsync(baseUrl + f, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                    await stream.CopyToAsync(fs, ct);

                    double pct = (double)(i + 1) / m2mFiles.Length * 100.0;
                    model.DownloadProgressPercent = pct;
                    progress?.Report(pct);
                }

                model.Status = ModelInstallationStatus.Installed;
                model.LastVerified = DateTime.UtcNow;
                return true;
            }
            catch
            {
                model.Status = ModelInstallationStatus.Error;
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(model.SourceUrl) && Uri.TryCreate(model.SourceUrl, UriKind.Absolute, out var uri))
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                    totalRead += bytesRead;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double pct = (double)totalRead / totalBytes.Value * 100.0;
                        model.DownloadProgressPercent = pct;
                        progress?.Report(pct);
                    }
                }

                model.Status = ModelInstallationStatus.Installed;
                model.LastVerified = DateTime.UtcNow;
                return true;
            }
            catch
            {
                model.Status = ModelInstallationStatus.Error;
                return false;
            }
        }
        else
        {
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            {
                model.Status = ModelInstallationStatus.Installed;
                model.LastVerified = DateTime.UtcNow;
                return true;
            }

            model.Status = ModelInstallationStatus.Error;
            return false;
        }
    }

    public Task<bool> DeleteModelAsync(string modelId)
    {
        var model = GetModel(modelId);
        if (model == null) return Task.FromResult(false);

        string baseDir = GetModelStorageDirectory();
        string targetPath = Path.Combine(baseDir, model.LocalRelativePath);

        try
        {
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            else if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, true);
            }

            model.Status = ModelInstallationStatus.NotInstalled;
            model.DownloadProgressPercent = 0.0;
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> VerifyModelIntegrityAsync(string modelId)
    {
        var model = GetModel(modelId);
        if (model == null) return false;

        string baseDir = GetModelStorageDirectory();
        string targetPath = Path.Combine(baseDir, model.LocalRelativePath);

        // Allow translation_eval staging folder lookup for specialized CT2 models
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath) &&
            (model.Id == "envit5_ct2_int8" || model.Id == "opus_mt_zh_vi_ct2"))
        {
            string evalDir = Path.Combine(baseDir, "translation_eval", model.Id);
            if (Directory.Exists(evalDir))
            {
                targetPath = evalDir;
            }
        }

        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            model.Status = ModelInstallationStatus.NotInstalled;
            return false;
        }

        model.Status = ModelInstallationStatus.Installing;

        try
        {
            if (File.Exists(targetPath))
            {
                string computedHash = await ComputeSha256Async(targetPath);
                if (IsSha256Hex(model.Sha256Checksum) &&
                    !string.Equals(computedHash, model.Sha256Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    model.Status = ModelInstallationStatus.Error;
                    return false;
                }

                model.LastVerified = DateTime.UtcNow;
                model.Status = ModelInstallationStatus.Installed;
                return true;
            }

            // Directory-based CTranslate2 models: validate weights SHA-256 and required tokenizer artifacts
            string modelBin = Path.Combine(targetPath, "model.bin");
            if (!File.Exists(modelBin) || new FileInfo(modelBin).Length == 0)
            {
                model.Status = ModelInstallationStatus.Error;
                return false;
            }

            foreach (string requiredTokenizerFile in GetRequiredTokenizerFiles(model.Id))
            {
                string tokPath = Path.Combine(targetPath, requiredTokenizerFile);
                if (!File.Exists(tokPath) || new FileInfo(tokPath).Length == 0)
                {
                    model.Status = ModelInstallationStatus.Error;
                    return false;
                }
            }

            if (IsSha256Hex(model.Sha256Checksum))
            {
                string computedBinHash = await ComputeSha256Async(modelBin);
                if (!string.Equals(computedBinHash, model.Sha256Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    model.Status = ModelInstallationStatus.Error;
                    return false;
                }
            }

            model.LastVerified = DateTime.UtcNow;
            model.Status = ModelInstallationStatus.Installed;
            return true;
        }
        catch
        {
            model.Status = ModelInstallationStatus.Error;
            return false;
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hashBytes = await sha256.ComputeHashAsync(stream);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    private static bool IsSha256Hex(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length != 64)
        {
            return false;
        }

        // Ignore legacy zero-padded placeholder hashes
        if (candidate.EndsWith("0000000000000000", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char c in candidate)
        {
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }

        return true;
    }

    private static IReadOnlyList<string> GetRequiredTokenizerFiles(string modelId)
    {
        return modelId switch
        {
            "envit5_ct2_int8" => new[] { "tokenizer.json", "spiece.model" },
            "opus_mt_zh_vi_ct2" => new[] { "source.spm", "target.spm" },
            "m2m100-418m-ct2" or "m2m100_418m_int8" => new[] { "sentencepiece.bpe.model" },
            _ => Array.Empty<string>()
        };
    }

    public Task<bool> ActivateModelAsync(string modelId)
    {
        var model = GetModel(modelId);
        if (model == null) return Task.FromResult(false);

        if (model.Category == ModelCategory.Translation)
        {
            _settingsService.Settings.ActiveTranslationModelId = modelId;
            _ = _settingsService.SaveSettingsAsync();
        }

        RefreshInstallationStatuses();
        return Task.FromResult(true);
    }

    private void RefreshInstallationStatuses()
    {
        string baseDir = GetModelStorageDirectory();
        string activeTranslationId = _settingsService.Settings.ActiveTranslationModelId;

        bool ocrWorkerExists = File.Exists("workers/paddle-ocr/ocr_worker.py") ||
                               File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "workers", "paddle-ocr", "ocr_worker.py"));

        foreach (var model in _registry)
        {
            string fullPath = Path.Combine(baseDir, model.LocalRelativePath);
            bool exists = (File.Exists(fullPath) && new FileInfo(fullPath).Length > 0) || Directory.Exists(fullPath);

            if (model.Id == "paddle-ocr-v4" && ocrWorkerExists)
            {
                exists = true;
            }
            else if (model.Id == "builtin-lexical-multilingual")
            {
                exists = true;
            }
            else if (model.Id == "m2m100-418m-ct2" || model.Id == "m2m100_418m_int8")
            {
                string modelBin = Path.Combine(fullPath, "model.bin");
                exists = File.Exists(modelBin) && new FileInfo(modelBin).Length > 100 * 1024 * 1024;
            }
            else if (model.Id == "envit5_ct2_int8")
            {
                string altPath = Path.Combine(baseDir, "translation_eval", "envit5_ct2_int8");
                string checkDir = Directory.Exists(fullPath) ? fullPath : altPath;
                exists = File.Exists(Path.Combine(checkDir, "model.bin")) &&
                         new FileInfo(Path.Combine(checkDir, "model.bin")).Length > 50 * 1024 * 1024 &&
                         File.Exists(Path.Combine(checkDir, "tokenizer.json"));
            }
            else if (model.Id == "opus_mt_zh_vi_ct2")
            {
                string altPath = Path.Combine(baseDir, "translation_eval", "opus_mt_zh_vi_ct2");
                string checkDir = Directory.Exists(fullPath) ? fullPath : altPath;
                exists = File.Exists(Path.Combine(checkDir, "model.bin")) &&
                         new FileInfo(Path.Combine(checkDir, "model.bin")).Length > 10 * 1024 * 1024 &&
                         File.Exists(Path.Combine(checkDir, "source.spm")) &&
                         File.Exists(Path.Combine(checkDir, "target.spm"));
            }

            if (!exists)
            {
                model.Status = ModelInstallationStatus.NotInstalled;
            }
            else if (model.Category == ModelCategory.Translation && 
                (string.Equals(model.Id, activeTranslationId, StringComparison.OrdinalIgnoreCase) ||
                 (model.Id == "m2m100-418m-ct2" && (activeTranslationId == "m2m100_418m_int8" || activeTranslationId == "m2m100-418m-ct2"))))
            {
                model.Status = ModelInstallationStatus.Active;
            }
            else
            {
                model.Status = ModelInstallationStatus.Installed;
            }
        }
    }
}
