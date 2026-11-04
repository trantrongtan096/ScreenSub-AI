using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Translation;

public sealed class AzureTranslatorProvider : ITranslationProvider
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly TranslationCache _cache;

    public string ProviderName => "Azure AI Translator";
    public bool IsMock => false;
    public ServiceStatus Status { get; private set; } = ServiceStatus.Stopped;

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

        // Check Cache first
        if (_cache.TryGet(text, source, targets, out var cachedResult) && cachedResult != null)
        {
            return cachedResult;
        }

        var settings = _settingsService.Settings;
        var apiKey = _settingsService.GetSecret("AzureApiKey");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = settings.AzureApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Status = ServiceStatus.Degraded;
            throw new InvalidOperationException("Chưa cấu hình Azure Translator API Key trong Cài đặt.");
        }

        var endpoint = string.IsNullOrWhiteSpace(settings.AzureEndpoint)
            ? "https://api.cognitive.microsofttranslator.com"
            : settings.AzureEndpoint.TrimEnd('/');

        // Build URL parameters
        var queryParams = new List<string> { "api-version=3.0" };

        var fromCode = MapSourceLanguageCode(source);
        if (!string.IsNullOrEmpty(fromCode))
        {
            queryParams.Add($"from={fromCode}");
        }

        foreach (var t in targets)
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
        if (!string.IsNullOrWhiteSpace(settings.AzureRegion))
        {
            request.Headers.Add("Ocp-Apim-Subscription-Region", settings.AzureRegion);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var errContent = await response.Content.ReadAsStringAsync(ct);
                Status = ServiceStatus.Error;
                throw new HttpRequestException($"Azure API trả về mã lỗi: {response.StatusCode} - {errContent}");
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
                            translationsDict[targetEnum.Value] = transText;
                        }
                    }
                }
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

            _cache.Set(text, source, targets, result);
            Status = ServiceStatus.Connected;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            Status = ServiceStatus.Error;
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
}
