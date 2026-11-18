using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Translation;
using Xunit;

namespace UnitTests;

public class ContextAwareTranslationTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;
        public int CallCount { get; private set; }
        public string? LastRequestBody { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (request.Content != null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            return await _handler(request);
        }
    }

    private static Mock<ISettingsService> CreateSettingsMock(AppSettings settings)
    {
        var mock = new Mock<ISettingsService>();
        mock.SetupGet(s => s.Settings).Returns(settings);
        mock.Setup(s => s.GetSecret("GeminiApiKey")).Returns(settings.GeminiApiKey);
        mock.Setup(s => s.GetSecret("AzureApiKey")).Returns(settings.AzureApiKey);
        return mock;
    }

    [Fact]
    public async Task GeminiProvider_WithContext_ShouldIncludeContextInPromptAndCacheResult()
    {
        var settings = new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Online,
            OnlineProvider = OnlineTranslationProviderType.Gemini,
            GeminiApiKey = "test-gemini-key",
            EnableContextAwareTranslation = true
        };
        var settingsMock = CreateSettingsMock(settings);

        var geminiResponsePayload = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = "{\"detected_lang\":\"en\",\"translations\":{\"vi\":\"Bọn mình chỉ đang uống chút gì đó thôi.\"}}"
                            }
                        }
                    }
                }
            }
        };

        var handler = new StubHttpMessageHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(geminiResponsePayload), Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        });

        using var httpClient = new HttpClient(handler);
        var cache = new TranslationCache();
        var provider = new GeminiTranslationProvider(httpClient, settingsMock.Object, cache);

        var context = new List<SubtitleItem>
        {
            new()
            {
                OriginalText = "Hey, what are you two doing here?",
                VietnameseText = "Này, hai cậu đang làm gì ở đây thế?",
                UpdatedAt = DateTime.UtcNow.AddSeconds(-3)
            }
        };

        var result1 = await provider.TranslateWithContextAsync(
            "We're just having a drink.",
            SourceLanguage.Auto,
            new[] { TargetLanguage.Vietnamese },
            context);

        result1.DetectedLanguage.Should().Be("en");
        result1.GetTranslation(TargetLanguage.Vietnamese).Should().Be("Bọn mình chỉ đang uống chút gì đó thôi.");
        result1.IsFromCache.Should().BeFalse();
        handler.CallCount.Should().Be(1);
        using var reqDoc = JsonDocument.Parse(handler.LastRequestBody!);
        var sentPrompt = reqDoc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString();
        sentPrompt.Should().Contain("Hey, what are you two doing here?");
        sentPrompt.Should().Contain("[Current Subtitle]: We're just having a drink.");

        // Second identical request with same context should hit cache
        var result2 = await provider.TranslateWithContextAsync(
            "We're just having a drink.",
            SourceLanguage.Auto,
            new[] { TargetLanguage.Vietnamese },
            context);

        result2.IsFromCache.Should().BeTrue();
        result2.GetTranslation(TargetLanguage.Vietnamese).Should().Be("Bọn mình chỉ đang uống chút gì đó thôi.");
        handler.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task GeminiProvider_StrictOfflineMode_ShouldThrowWithoutNetworkCall()
    {
        var settings = new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Offline,
            GeminiApiKey = "test-gemini-key"
        };
        var settingsMock = CreateSettingsMock(settings);

        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Network should never be called in Offline Mode"));
        using var httpClient = new HttpClient(handler);
        var provider = new GeminiTranslationProvider(httpClient, settingsMock.Object, new TranslationCache());

        Func<Task> act = () => provider.TranslateAsync("Hello", SourceLanguage.English, new[] { TargetLanguage.Vietnamese });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Offline*");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GeminiProvider_RateLimited429_ShouldDegradeWithoutFallingBackToMock()
    {
        var settings = new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Online,
            OnlineProvider = OnlineTranslationProviderType.Gemini,
            GeminiApiKey = "test-gemini-key"
        };
        var settingsMock = CreateSettingsMock(settings);

        var handler = new StubHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));

        using var httpClient = new HttpClient(handler);
        var provider = new GeminiTranslationProvider(httpClient, settingsMock.Object, new TranslationCache());

        var result = await provider.TranslateAsync("Hello", SourceLanguage.English, new[] { TargetLanguage.Vietnamese });

        provider.IsMock.Should().BeFalse();
        provider.Status.Should().Be(ServiceStatus.Degraded);
        result.GetTranslation(TargetLanguage.Vietnamese).Should().StartWith("[Gemini 429");
    }

    [Fact]
    public void TranslationProviderResolver_ShouldRouteToGeminiOnlyWhenSelectedInOnlineMode()
    {
        var settings = new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Offline,
            OnlineProvider = OnlineTranslationProviderType.Gemini,
            GeminiApiKey = "test-gemini-key",
            AzureApiKey = "test-azure-key",
            AzureRegion = "eastasia"
        };
        var settingsMock = CreateSettingsMock(settings);

        var cache = new TranslationCache();
        using var http = new HttpClient(new StubHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var local = new LocalTranslationProvider(new LocalNeuralTranslationEngine(), cache, settingsMock.Object);
        var azure = new AzureTranslatorProvider(http, settingsMock.Object, cache);
        var gemini = new GeminiTranslationProvider(http, settingsMock.Object, cache);
        var mock = new MockTranslationProvider();

        var resolver = new TranslationProviderResolver(settingsMock.Object, local, azure, mock, gemini);

        // Strict Offline ignores Gemini & Azure keys
        resolver.ResolveActiveProvider().Should().BeSameAs(local);
        resolver.IsStrictOffline.Should().BeTrue();

        // Online + Gemini selected -> Gemini
        settings.TranslationEngineMode = TranslationEngineMode.Online;
        resolver.ResolveActiveProvider().Should().BeSameAs(gemini);

        // Online + Azure selected -> Azure
        settings.OnlineProvider = OnlineTranslationProviderType.Azure;
        resolver.ResolveActiveProvider().Should().BeSameAs(azure);
    }

    [Fact]
    public async Task AzureTranslatorProvider_ShouldSupportGlobalEmptyRegionAndTrimWhitespaceKey()
    {
        var settings = new AppSettings
        {
            AzureApiKey = "  global-azure-key  ",
            AzureRegion = "   ",
            AzureEndpoint = "https://api.cognitive.microsofttranslator.com/"
        };
        var settingsMock = CreateSettingsMock(settings);

        bool hadRegionHeader = false;
        string? capturedKeyHeader = null;
        string? capturedUrl = null;
        var handler = new StubHttpMessageHandler(req =>
        {
            hadRegionHeader = req.Headers.Contains("Ocp-Apim-Subscription-Region");
            capturedKeyHeader = req.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var vals)
                ? vals.FirstOrDefault()
                : null;
            capturedUrl = req.RequestUri?.ToString();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"detectedLanguage\":{\"language\":\"en\"},\"translations\":[{\"text\":\"Xin chào\",\"to\":\"vi\"}]}]")
            });
        });

        using var http = new HttpClient(handler);
        var azure = new AzureTranslatorProvider(http, settingsMock.Object, new TranslationCache());

        azure.IsConfigured.Should().BeTrue();
        var (ok, _) = await azure.TestConnectionAsync();

        ok.Should().BeTrue();
        hadRegionHeader.Should().BeFalse();
        capturedKeyHeader.Should().Be("global-azure-key");
        capturedUrl.Should().StartWith("https://api.cognitive.microsofttranslator.com/translate?");
    }

    [Fact]
    public async Task SettingsService_ShouldProtectAndDeleteAzureAndGeminiKeys()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ScreenSubSecrets_{Guid.NewGuid():N}");
        try
        {
            var svc = new ScreenSub.Infrastructure.SettingsService(tempDir);
            svc.Settings.AzureApiKey = " azure-secret-123 ";
            svc.Settings.GeminiApiKey = " gemini-secret-456 ";
            await svc.SaveSettingsAsync();

            var settingsJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "settings.json"));
            settingsJson.Should().NotContain("azure-secret-123");
            settingsJson.Should().NotContain("gemini-secret-456");

            var reloaded = new ScreenSub.Infrastructure.SettingsService(tempDir);
            await reloaded.LoadSettingsAsync();
            reloaded.Settings.AzureApiKey.Should().Be("azure-secret-123");
            reloaded.Settings.GeminiApiKey.Should().Be("gemini-secret-456");

            reloaded.Settings.AzureApiKey = string.Empty;
            reloaded.Settings.GeminiApiKey = string.Empty;
            await reloaded.SaveSettingsAsync();

            var cleared = new ScreenSub.Infrastructure.SettingsService(tempDir);
            await cleared.LoadSettingsAsync();
            cleared.Settings.AzureApiKey.Should().BeEmpty();
            cleared.Settings.GeminiApiKey.Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
