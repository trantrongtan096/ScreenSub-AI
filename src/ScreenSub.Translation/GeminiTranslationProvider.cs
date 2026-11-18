using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Core.Processing;

namespace ScreenSub.Translation;

/// <summary>
/// Optional Online Context-Aware AI Translation Provider powered by Google Gemini.
/// Uses recent dialogue history purely for pronoun, gender, and conversational tone consistency,
/// translating ONLY the current subtitle line. Never sends cloud requests in Offline Mode.
/// </summary>
public sealed class GeminiTranslationProvider : IContextAwareTranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly TranslationCache _cache;
    private DateTime _rateLimitedUntilUtc = DateTime.MinValue;

    public TranslationCache Cache => _cache;
    public string ProviderName => "Gemini AI (Contextual)";
    public bool IsMock => false;
    public bool IsOffline => false;

    private ServiceStatus? _runtimeStatus;
    private string? _runtimeStatusMessage;

    public bool IsConfigured
    {
        get
        {
            var apiKey = ResolveApiKey();
            return !string.IsNullOrWhiteSpace(apiKey);
        }
    }

    public ServiceStatus Status
    {
        get
        {
            if (_runtimeStatus.HasValue) return _runtimeStatus.Value;
            return IsConfigured ? ServiceStatus.Running : ServiceStatus.Degraded;
        }
        private set => _runtimeStatus = value;
    }

    public string StatusMessage
    {
        get
        {
            if (!string.IsNullOrEmpty(_runtimeStatusMessage)) return _runtimeStatusMessage;
            return IsConfigured ? "Đã cấu hình Gemini API" : "Chưa cấu hình Gemini API Key";
        }
        private set => _runtimeStatusMessage = value;
    }

    public GeminiTranslationProvider(HttpClient httpClient, ISettingsService settingsService, TranslationCache cache)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _cache = cache;
        if (_httpClient.Timeout > TimeSpan.FromSeconds(10) || _httpClient.Timeout <= TimeSpan.Zero)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(6);
        }
    }

    public Task<TranslationResult> TranslateAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct = default)
    {
        return TranslateWithContextAsync(text, source, targets, Array.Empty<SubtitleItem>(), ct);
    }

    public async Task<TranslationResult> TranslateWithContextAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        IReadOnlyList<SubtitleItem> recentContext,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Empty;
        }

        var settings = _settingsService.Settings;
        if (settings.TranslationEngineMode == TranslationEngineMode.Offline)
        {
            throw new InvalidOperationException("Không thể gọi Gemini Cloud API khi đang ở chế độ Strict Offline.");
        }

        text = SubtitleTextProcessor.RestoreWordSpacing(text.Trim());

        var validContext = FilterRecentContext(text, recentContext);
        string? contextSignature = BuildContextSignature(validContext);
        string model = string.IsNullOrWhiteSpace(settings.GeminiModel) ? "gemini-2.5-flash" : settings.GeminiModel.Trim();
        string cacheIdentity = $"Gemini|model={model}|post=v2-general";

        // 1. Check Cache
        if (_cache.TryGet(text, source, targets, out var cachedResult, contextSignature, cacheIdentity) && cachedResult != null)
        {
            Status = ServiceStatus.Connected;
            StatusMessage = "Gemini AI (Cache Hit)";
            return cachedResult;
        }

        // 2. Handle same-language direct pass-through
        var effectiveTargets = targets
            .Where(t => !IsSameLanguage(source, t))
            .Distinct()
            .ToList();

        if (effectiveTargets.Count == 0 && source != SourceLanguage.Auto)
        {
            var directTranslations = targets.ToDictionary(t => t, _ => text);
            var immediate = new TranslationResult
            {
                OriginalText = text,
                DetectedLanguage = MapLanguageCode(source) ?? "auto",
                Translations = directTranslations,
                LatencyMs = 0,
                Timestamp = DateTime.UtcNow,
                IsFromCache = false,
                ProviderName = ProviderName
            };
            _cache.Set(text, source, targets, immediate, contextSignature, cacheIdentity);
            return immediate;
        }

        // 3. Validate API Key & Rate-limit cooldown
        string apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Status = ServiceStatus.Degraded;
            StatusMessage = "Chưa cấu hình Gemini API Key";
            return CreateErrorResult(text, targets, "[Gemini: Chưa cấu hình API Key]");
        }

        if (DateTime.UtcNow < _rateLimitedUntilUtc)
        {
            Status = ServiceStatus.Degraded;
            StatusMessage = "Gemini API tạm giới hạn tốc độ (429)";
            return CreateErrorResult(text, targets, "[Gemini: Đang giới hạn tốc độ, vui lòng đợi]");
        }

        // 4. Build Gemini REST request
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";

        string prompt = BuildContextualPrompt(text, source, effectiveTargets, validContext);
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                responseMimeType = "application/json"
            }
        };

        var sw = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));

            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            sw.Stop();

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _rateLimitedUntilUtc = DateTime.UtcNow.AddSeconds(15);
                Status = ServiceStatus.Degraded;
                StatusMessage = "Gemini API quá tải (429)";
                return CreateErrorResult(text, targets, "[Gemini 429: Vượt giới hạn request]");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Status = ServiceStatus.Error;
                StatusMessage = "Gemini API Key không hợp lệ";
                return CreateErrorResult(text, targets, "[Gemini Auth Error: Kiểm tra API Key]");
            }

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var (detectedLang, parsedTranslations) = ParseGeminiResponse(json, text, source, targets, effectiveTargets);

            var result = new TranslationResult
            {
                OriginalText = text,
                DetectedLanguage = detectedLang,
                Translations = parsedTranslations,
                LatencyMs = sw.ElapsedMilliseconds,
                Timestamp = DateTime.UtcNow,
                IsFromCache = false,
                ProviderName = ProviderName
            };

            Status = ServiceStatus.Connected;
            StatusMessage = $"Gemini AI ({sw.ElapsedMilliseconds} ms)";
            _cache.Set(text, source, targets, result, contextSignature, cacheIdentity);

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Status = ServiceStatus.Degraded;
            StatusMessage = "Gemini API Timeout";
            return CreateErrorResult(text, targets, "[Gemini Timeout]");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Never log private transcript content in status or error messages
            Status = ServiceStatus.Error;
            StatusMessage = "Lỗi kết nối Gemini API";
            return CreateErrorResult(text, targets, "[Gemini: Lỗi kết nối]");
        }
    }

    private string ResolveApiKey()
    {
        var key = _settingsService.GetSecret("GeminiApiKey");
        if (string.IsNullOrWhiteSpace(key))
        {
            key = _settingsService.Settings.GeminiApiKey;
        }
        return key?.Trim() ?? string.Empty;
    }

    private static IReadOnlyList<SubtitleItem> FilterRecentContext(string currentText, IReadOnlyList<SubtitleItem> recentContext)
    {
        if (recentContext == null || recentContext.Count == 0)
        {
            return Array.Empty<SubtitleItem>();
        }

        var cutoff = DateTime.UtcNow.AddSeconds(-60);
        return recentContext
            .Where(item =>
                item != null &&
                item.HasContent &&
                item.UpdatedAt >= cutoff &&
                !string.Equals(item.OriginalText.Trim(), currentText, StringComparison.OrdinalIgnoreCase))
            .TakeLast(3)
            .ToList();
    }

    private static string? BuildContextSignature(IReadOnlyList<SubtitleItem> context)
    {
        if (context.Count == 0) return null;
        var joined = string.Join("|", context.Select(c => c.OriginalText.Trim()));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash, 0, 4);
    }

    private static string BuildContextualPrompt(
        string currentText,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        IReadOnlyList<SubtitleItem> context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a professional subtitle translator for movies, videos, and natural spoken dialogue.");
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Translate ONLY [Current Subtitle] into the requested target languages.");
        sb.AppendLine("2. NEVER repeat or include sentences from [Recent Context] in your output.");
        sb.AppendLine("3. For Vietnamese ('vi'), use natural, colloquial spoken Vietnamese matching the dialogue context (pronouns, tone, idioms). Avoid literal word-for-word translation.");
        sb.AppendLine("4. Output valid JSON ONLY: {\"detected_lang\":\"en|zh|vi\",\"translations\":{\"vi\":\"...\"}}.");
        sb.AppendLine();

        if (context.Count > 0)
        {
            sb.AppendLine("[Recent Context] (for pronoun & tone reference only, DO NOT translate or repeat):");
            for (int i = 0; i < context.Count; i++)
            {
                var c = context[i];
                string refVi = string.IsNullOrWhiteSpace(c.VietnameseText) ? string.Empty : $" -> {c.VietnameseText}";
                sb.AppendLine($"- {c.OriginalText}{refVi}");
            }
            sb.AppendLine();
        }

        string srcHint = source == SourceLanguage.Auto ? "auto-detect" : MapLanguageCode(source) ?? "auto";
        string tgtCodes = string.Join(", ", targets.Select(MapTargetCode));
        sb.AppendLine($"Source Language: {srcHint}");
        sb.AppendLine($"Target Languages: {tgtCodes}");
        sb.AppendLine($"[Current Subtitle]: {currentText}");

        return sb.ToString();
    }

    private static (string DetectedLang, Dictionary<TargetLanguage, string> Translations) ParseGeminiResponse(
        string responseJson,
        string originalText,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> allTargets,
        IReadOnlyList<TargetLanguage> requestedTargets)
    {
        var result = new Dictionary<TargetLanguage, string>();
        foreach (var t in allTargets)
        {
            if (IsSameLanguage(source, t))
            {
                result[t] = originalText;
            }
        }

        string detectedLang = MapLanguageCode(source) ?? SubtitleTextProcessor.DetectLanguage(originalText);

        using var doc = JsonDocument.Parse(responseJson);
        string? rawModelText = null;
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) &&
            candidates.ValueKind == JsonValueKind.Array &&
            candidates.GetArrayLength() > 0)
        {
            var first = candidates[0];
            if (first.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array &&
                parts.GetArrayLength() > 0)
            {
                rawModelText = parts[0].GetProperty("text").GetString();
            }
        }

        if (string.IsNullOrWhiteSpace(rawModelText))
        {
            throw new InvalidOperationException("Empty Gemini response");
        }

        // Strip markdown fences if any
        string jsonBody = rawModelText.Trim();
        if (jsonBody.StartsWith("```"))
        {
            int firstNewline = jsonBody.IndexOf('\n');
            int lastFence = jsonBody.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                jsonBody = jsonBody.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
            }
        }

        using var inner = JsonDocument.Parse(jsonBody);
        if (inner.RootElement.TryGetProperty("detected_lang", out var dlProp))
        {
            string? dl = dlProp.GetString();
            if (!string.IsNullOrWhiteSpace(dl))
            {
                detectedLang = dl.Trim().ToLowerInvariant();
            }
        }

        if (inner.RootElement.TryGetProperty("translations", out var transObj) &&
            transObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var target in requestedTargets)
            {
                string code = MapTargetCode(target);
                if (transObj.TryGetProperty(code, out var valProp))
                {
                    string rawVal = valProp.GetString() ?? string.Empty;
                    result[target] = SubtitleTextProcessor.CleanTranslationOutput(rawVal);
                }
            }
        }

        foreach (var target in allTargets)
        {
            if (!result.ContainsKey(target))
            {
                result[target] = string.Empty;
            }
        }

        return (detectedLang, result);
    }

    private TranslationResult CreateErrorResult(string originalText, IReadOnlyList<TargetLanguage> targets, string errorMessage)
    {
        return new TranslationResult
        {
            OriginalText = originalText,
            DetectedLanguage = "auto",
            Translations = targets.ToDictionary(t => t, _ => errorMessage),
            LatencyMs = 0,
            Timestamp = DateTime.UtcNow,
            IsFromCache = false,
            ProviderName = ProviderName
        };
    }

    private static bool IsSameLanguage(SourceLanguage src, TargetLanguage tgt)
    {
        return (src == SourceLanguage.English && tgt == TargetLanguage.English) ||
               (src == SourceLanguage.Chinese && tgt == TargetLanguage.Chinese) ||
               (src == SourceLanguage.Vietnamese && tgt == TargetLanguage.Vietnamese);
    }

    private static string? MapLanguageCode(SourceLanguage lang) => lang switch
    {
        SourceLanguage.English => "en",
        SourceLanguage.Chinese => "zh",
        SourceLanguage.Vietnamese => "vi",
        _ => null
    };

    private static string MapTargetCode(TargetLanguage lang) => lang switch
    {
        TargetLanguage.English => "en",
        TargetLanguage.Chinese => "zh",
        TargetLanguage.Vietnamese => "vi",
        _ => "vi"
    };

    /// <summary>
    /// Verifies live Gemini API connectivity and API key validity.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "Chưa nhập Gemini API Key.");
        }

        var settings = _settingsService.Settings;
        string model = string.IsNullOrWhiteSpace(settings.GeminiModel) ? "gemini-2.5-flash" : settings.GeminiModel.Trim();
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = "Translate 'Hello' to Vietnamese as JSON {\"vi\":\"...\"}" } }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                responseMimeType = "application/json"
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                Status = ServiceStatus.Connected;
                StatusMessage = "Đã kết nối Gemini AI";
                return (true, $"Kết nối Gemini ({model}) thành công.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (false, "Lỗi xác thực Gemini (401/403): API Key không hợp lệ hoặc bị từ chối.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (false, $"Lỗi 404: Không tìm thấy model Gemini '{model}'.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return (false, "Lỗi 429: Gemini API đang vượt quota.");
            }

            return (false, $"Gemini API trả về mã {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        catch (Exception ex)
        {
            return (false, $"Không kết nối được Gemini: {ex.Message}");
        }
    }
}
