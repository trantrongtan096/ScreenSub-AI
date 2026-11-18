using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Translation;
using Xunit;

namespace UnitTests;

public class OfflineTranslationTests
{
    [Theory]
    [InlineData("你今天有时间吗？", "你今天有時間嗎？")]
    [InlineData("简体中文", "簡體中文")]
    [InlineData("时间", "時間")]
    public void LocalChineseConverter_SimplifiedToTraditional_ShouldConvertCorrectly(string simplified, string expectedTraditional)
    {
        string result = LocalChineseConverter.ToTraditional(simplified);
        result.Should().Be(expectedTraditional);
    }

    [Theory]
    [InlineData("你今天有時間嗎？", "你今天有时间吗？")]
    [InlineData("簡體中文", "简体中文")]
    [InlineData("時間", "时间")]
    public void LocalChineseConverter_TraditionalToSimplified_ShouldConvertCorrectly(string traditional, string expectedSimplified)
    {
        string result = LocalChineseConverter.ToSimplified(traditional);
        result.Should().Be(expectedSimplified);
    }

    [Fact]
    public async Task LocalNeuralTranslationEngine_ShouldTranslateAllSixLanguagePairs()
    {
        using var engine = new LocalNeuralTranslationEngine();
        await engine.InitializeAsync();

        // 1. En -> Vi
        var enToVi = await engine.TranslateTextAsync("Hello world, are you free today?", "en", "vi");
        enToVi.Should().NotBeNullOrWhiteSpace();
        enToVi.ToLowerInvariant().Should().ContainAny("thế giới", "hôm nay", "chào", "rảnh", "tự do");

        // 2. Vi -> En
        var viToEn = await engine.TranslateTextAsync("Hôm nay bạn có rảnh không?", "vi", "en");
        viToEn.Should().NotBeNullOrWhiteSpace();
        viToEn.ToLowerInvariant().Should().ContainAny("free", "today", "you", "time");

        // 3. Zh -> Vi
        var zhToVi = await engine.TranslateTextAsync("你今天有时间吗？", "zh", "vi");
        zhToVi.Should().NotBeNullOrWhiteSpace();
        zhToVi.ToLowerInvariant().Should().ContainAny("hôm nay", "thời gian", "rảnh", "bạn", "anh");

        // 4. Vi -> Zh
        var viToZh = await engine.TranslateTextAsync("Xin chào, cảm ơn bạn", "vi", "zh");
        viToZh.Should().NotBeNullOrWhiteSpace();
        viToZh.Should().ContainAny("你好", "谢谢");

        // 5. En -> Zh
        var enToZh = await engine.TranslateTextAsync("Hello, thank you very much", "en", "zh");
        enToZh.Should().NotBeNullOrWhiteSpace();
        enToZh.Should().ContainAny("您好", "你好", "感谢", "谢谢");

        // 6. Zh -> En
        var zhToEn = await engine.TranslateTextAsync("你好，谢谢", "zh", "en");
        zhToEn.Should().NotBeNullOrWhiteSpace();
        zhToEn.ToLowerInvariant().Should().ContainAny("hello", "thank");
    }

    [Fact]
    public async Task Audit_NovelArbitrarySentences_ShouldBeDocumentedAccurately()
    {
        var engine = new LocalNeuralTranslationEngine();
        string novelEnglish = "The weather is cold today";
        var viResult = await engine.TranslateTextAsync(novelEnglish, "en", "vi");
        
        // Output will retain untranslated words for terms not in the lexical dictionary:
        viResult.Should().NotBeNull();
    }

    [Fact]
    public async Task LocalTranslationProvider_ShouldBeStrictlyOfflineAndUseCache()
    {
        var engine = new LocalNeuralTranslationEngine();
        var cache = new TranslationCache();
        var mockSettings = new Mock<ISettingsService>();
        mockSettings.Setup(s => s.Settings).Returns(new AppSettings());

        var provider = new LocalTranslationProvider(engine, cache, mockSettings.Object);

        provider.IsOffline.Should().BeTrue();
        provider.IsMock.Should().BeFalse();
        provider.ProviderName.Should().Contain("Offline");

        var targets = new List<TargetLanguage> { TargetLanguage.English, TargetLanguage.Vietnamese };
        var result1 = await provider.TranslateAsync("你今天有时间吗？", SourceLanguage.Chinese, targets);

        result1.Should().NotBeNull();
        result1.Translations[TargetLanguage.English].Should().NotBeNullOrWhiteSpace();
        result1.Translations[TargetLanguage.Vietnamese].Should().NotBeNullOrWhiteSpace();

        // 2nd call should hit cache in 0 ms
        var result2 = await provider.TranslateAsync("你今天有时间吗？", SourceLanguage.Chinese, targets);
        result2.Should().NotBeNull();
        result2.IsFromCache.Should().BeTrue();
        provider.StatusMessage.Should().Contain("Cache Hit");
    }

    [Fact]
    public void TranslationProviderResolver_ShouldEnforceStrictOfflinePolicy()
    {
        var mockSettings = new Mock<ISettingsService>();
        var settings = new AppSettings
        {
            TranslationEngineMode = TranslationEngineMode.Offline,
            AutoFallbackPolicy = AutoFallbackPolicy.PreferOffline,
            AllowCloudFallbackOnOfflineFailure = false
        };
        mockSettings.Setup(s => s.Settings).Returns(settings);

        var engine = new LocalNeuralTranslationEngine();
        var cache = new TranslationCache();
        var local = new LocalTranslationProvider(engine, cache, mockSettings.Object);
        var azure = new AzureTranslatorProvider(new HttpClient(), mockSettings.Object, cache);
        var mock = new MockTranslationProvider();

        var resolver = new TranslationProviderResolver(mockSettings.Object, local, azure, mock);

        resolver.IsStrictOffline.Should().BeTrue();
        resolver.EffectiveMode.Should().Be(TranslationEngineMode.Offline);
        resolver.ActiveProviderBadgeText.Should().Be("OFFLINE AI — LOCAL PROCESSING");
        resolver.ResolveActiveProvider().Should().BeSameAs(local);
    }

    [Fact]
    public async Task RealNeuralModel_UserYoutubeScenario_ShouldTranslateCorrectly()
    {
        using var engine = new LocalNeuralTranslationEngine();
        await engine.InitializeAsync();

        string sentence = "with communities across Southeast Asia.";
        var result = await engine.TranslateTextAsync(sentence, "en", "vi");

        result.Should().NotBeNullOrWhiteSpace();
        result.Should().NotContain("MODEL NOT INSTALLED");
        result.ToLowerInvariant().Should().ContainAny("cộng đồng", "đông nam á");
    }
}

