using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Core.Processing;

namespace ScreenSub.Translation;

public sealed class AzureTranslatorProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly TranslationCache _cache;

    public TranslationCache Cache => _cache;
    public string ProviderName => "Azure AI Translator";
    public bool IsMock => false;

    private ServiceStatus? _runtimeStatus;
    private string? _runtimeStatusMessage;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    private string ResolveApiKey()
    {
        var apiKey = _settingsService.GetSecret("AzureApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = _settingsService.Settings.AzureApiKey;
        }
        return apiKey?.Trim() ?? string.Empty;
    }

    private string ResolveRegion() => _settingsService.Settings.AzureRegion?.Trim() ?? string.Empty;

    private string ResolveEndpoint()
    {
        var ep = _settingsService.Settings.AzureEndpoint?.Trim();
        return string.IsNullOrWhiteSpace(ep)
            ? "https://api.cognitive.microsofttranslator.com"
            : ep.TrimEnd('/');
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
            return IsConfigured ? "Đã cấu hình Azure API" : "Chưa cấu hình API Key";
        }
        private set => _runtimeStatusMessage = value;
    }

    public AzureTranslatorProvider(HttpClient httpClient, ISettingsService settingsService, TranslationCache cache)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _cache = cache;
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        SourceLanguage source,
        IReadOnlyList<TargetLanguage> targets,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return TranslationResult.Empty;
        }

        text = SubtitleTextProcessor.RestoreWordSpacing(text.Trim());
        string cacheIdentity = "AzureTranslator|api=3.0|post=v2-general";

        // Check Cache first
        if (_cache.TryGet(text, source, targets, out var cachedResult, contextSignature: null, cacheIdentity: cacheIdentity) && cachedResult != null)
        {
            return cachedResult;
        }

        // Filter out same-language translation if source language is explicitly known (Rule: Don't request Azure to translate to the same language)
        var effectiveTargets = targets
            .Where(t => !IsSameLanguage(source, t))
            .Distinct()
            .ToList();

        if (effectiveTargets.Count == 0 && source != SourceLanguage.Auto)
        {
            var directTranslations = new Dictionary<TargetLanguage, string>();
            foreach (var t in targets)
            {
                directTranslations[t] = text;
            }

            var immediateResult = new TranslationResult
            {
                OriginalText = text,
                DetectedLanguage = MapSourceLanguageCode(source) ?? string.Empty,
                Translations = directTranslations,
                LatencyMs = 0,
                Timestamp = DateTime.UtcNow,
                IsFromCache = false,
                ProviderName = ProviderName
            };

            _cache.Set(text, source, targets, immediateResult, contextSignature: null, cacheIdentity: cacheIdentity);
            return immediateResult;
        }

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Status = ServiceStatus.Degraded;
            StatusMessage = "Chưa cấu hình API Key";
            throw new InvalidOperationException("Chưa cấu hình Azure Translator API Key trong tab Cài đặt.");
        }

        var endpoint = ResolveEndpoint();
        var region = ResolveRegion();

        // Build URL parameters
        var queryParams = new List<string> { "api-version=3.0" };

        var fromCode = MapSourceLanguageCode(source);
        if (!string.IsNullOrEmpty(fromCode))
        {
            queryParams.Add($"from={fromCode}");
        }

        foreach (var t in effectiveTargets)
        {
            var toCode = MapTargetLanguageCode(t);
            if (!string.IsNullOrEmpty(toCode))
            {
                queryParams.Add($"to={toCode}");
            }
        }

        var url = $"{endpoint}/translate?{string.Join("&", queryParams)}";
        var requestBody = JsonSerializer.Serialize(new[] { new { Text = text } });

        var sw = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
        if (!string.IsNullOrWhiteSpace(region))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Region", region);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(ct);
                Status = ServiceStatus.Error;

                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                {
                    StatusMessage = "Lỗi 401/403: Sai API Key hoặc Azure Region không khớp";
                }
                else if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    StatusMessage = "Lỗi 404: Sai Azure Endpoint URL";
                }
                else if ((int)response.StatusCode == 429)
                {
                    StatusMessage = "Lỗi giới hạn (429): Vượt quá quota request";
                }
                else
                {
                    StatusMessage = $"Lỗi Azure API: {(int)response.StatusCode}";
                }

                throw new HttpRequestException($"Azure API trả về lỗi: {response.StatusCode} - {errContent}");
            }

            var jsonResponse = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(jsonResponse);

            var translationsDict = new Dictionary<TargetLanguage, string>();
            string detectedLang = string.Empty;

            var firstItem = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (firstItem.ValueKind != JsonValueKind.Undefined)
            {
                if (firstItem.TryGetProperty("detectedLanguage", out var detObj))
                {
                    detectedLang = detObj.GetProperty("language").GetString() ?? string.Empty;
                }

                if (firstItem.TryGetProperty("translations", out var transArray))
                {
                    foreach (var transItem in transArray.EnumerateArray())
                    {
                        var toLangCode = transItem.GetProperty("to").GetString();
                        var transText = transItem.GetProperty("text").GetString() ?? string.Empty;
                        var targetEnum = MapCodeToTargetLanguage(toLangCode);

                        if (targetEnum.HasValue)
                        {
                            translationsDict[targetEnum.Value] = SubtitleTextProcessor.RestoreWordSpacing(SubtitleTextProcessor.CleanTranslationOutput(transText));
                        }
                    }
                }
            }

            // Populate source language text into the dictionary if target requested it
            if (source == SourceLanguage.English || detectedLang.Equals("en", StringComparison.OrdinalIgnoreCase))
            {
                if (targets.Contains(TargetLanguage.English)) translationsDict[TargetLanguage.English] = text;
            }
            if (source == SourceLanguage.Chinese || detectedLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                if (targets.Contains(TargetLanguage.Chinese)) translationsDict[TargetLanguage.Chinese] = text;
            }
            if (source == SourceLanguage.Vietnamese || detectedLang.Equals("vi", StringComparison.OrdinalIgnoreCase))
            {
                if (targets.Contains(TargetLanguage.Vietnamese)) translationsDict[TargetLanguage.Vietnamese] = text;
            }

            var result = new TranslationResult
            {
                OriginalText = text,
                DetectedLanguage = detectedLang,
                Translations = translationsDict,
                LatencyMs = sw.ElapsedMilliseconds,
                Timestamp = DateTime.UtcNow,
                IsFromCache = false,
                ProviderName = ProviderName
            };

            _cache.Set(text, source, targets, result, contextSignature: null, cacheIdentity: cacheIdentity);
            Status = ServiceStatus.Connected;
            StatusMessage = "Đã kết nối";
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            Status = ServiceStatus.Error;
            if (string.IsNullOrEmpty(StatusMessage) || StatusMessage == "Đã kết nối")
            {
                StatusMessage = "Lỗi mạng: Không kết nối được Azure API";
            }
            throw;
        }
        catch (Exception ex)
        {
            Status = ServiceStatus.Error;
            StatusMessage = $"Lỗi: {ex.Message}";
            throw;
        }
    }

    private static string? MapSourceLanguageCode(SourceLanguage source) => source switch
    {
        SourceLanguage.English => "en",
        SourceLanguage.Chinese => "zh-Hans",
        SourceLanguage.Vietnamese => "vi",
        _ => null // Auto detect
    };

    private static string MapTargetLanguageCode(TargetLanguage target) => target switch
    {
        TargetLanguage.English => "en",
        TargetLanguage.Chinese => "zh-Hans",
        TargetLanguage.Vietnamese => "vi",
        _ => "vi"
    };

    private static TargetLanguage? MapCodeToTargetLanguage(string? code) => code switch
    {
        "en" => TargetLanguage.English,
        "zh" or "zh-Hans" or "zh-Hant" => TargetLanguage.Chinese,
        "vi" => TargetLanguage.Vietnamese,
        _ => null
    };

    private static bool IsSameLanguage(SourceLanguage source, TargetLanguage target) => (source, target) switch
    {
        (SourceLanguage.English, TargetLanguage.English) => true,
        (SourceLanguage.Chinese, TargetLanguage.Chinese) => true,
        (SourceLanguage.Vietnamese, TargetLanguage.Vietnamese) => true,
        _ => false
    };

    /// <summary>
    /// Verifies live Azure Translator connectivity and credentials without hitting the cache.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return (false, "Chưa nhập Azure Translator API Key.");
        }

        var endpoint = ResolveEndpoint();
        var region = ResolveRegion();
        var url = $"{endpoint}/translate?api-version=3.0&from=en&to=vi";
        var requestBody = JsonSerializer.Serialize(new[] { new { Text = "Hello" } });

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        request.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
        if (!string.IsNullOrWhiteSpace(region))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Region", region);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                Status = ServiceStatus.Connected;
                StatusMessage = "Đã kết nối Azure";
                return (true, $"Kết nối Azure thành công (Region: {(string.IsNullOrEmpty(region) ? "Global" : region)}).");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                Status = ServiceStatus.Error;
                StatusMessage = "Lỗi 401/403: Sai API Key hoặc Region";
                return (false, "Lỗi xác thực (401/403): API Key hoặc Azure Region (ví dụ eastasia / southeastasia / global) không khớp với tài nguyên Azure.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (false, $"Lỗi 404: Sai Azure Endpoint ({endpoint}).");
            }

            if ((int)response.StatusCode == 429)
            {
                return (false, "Lỗi 429: Tài khoản Azure đang vượt quá giới hạn quota.");
            }

            return (false, $"Azure API trả về mã {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        catch (Exception ex)
        {
            return (false, $"Không kết nối được Azure: {ex.Message}");
        }
    }
}
