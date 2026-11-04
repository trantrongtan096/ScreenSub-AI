using FluentAssertions;
using ScreenSub.Core.Enums;
using ScreenSub.Infrastructure;
using ScreenSub.Translation;
using Xunit;

namespace IntegrationTests;

public class WindowsSecretStoreTests
{
    [Fact]
    public void ProtectAndUnprotect_ShouldRecoverOriginalSecret()
    {
        var store = new WindowsSecretStore();
        string secretApiKey = "my-super-secret-azure-key-123456";

        string encrypted = store.Protect(secretApiKey);
        encrypted.Should().NotBe(secretApiKey);
        encrypted.Should().NotBeEmpty();

        string decrypted = store.Unprotect(encrypted);
        decrypted.Should().Be(secretApiKey);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public async Task SaveAndLoad_ShouldPersistSettings()
    {
        var service = new SettingsService();
        service.Settings.AzureRegion = "southeastasia";
        service.Settings.UseMockTranslation = true;

        service.SetSecret("TestIntegrationKey", "test-val-999");
        await service.SaveSettingsAsync();

        var loadedService = new SettingsService();
        await loadedService.LoadSettingsAsync();

        loadedService.GetSecret("TestIntegrationKey").Should().Be("test-val-999");
        loadedService.Settings.AzureRegion.Should().Be("southeastasia");
    }
}

public class MockTranslationProviderTests
{
    private readonly MockTranslationProvider _provider = new();

    [Fact]
    public async Task TranslateAsync_ShouldTranslateChineseSentenceToEnglishAndVietnamese()
    {
        string chineseText = "你今天有时间吗？";
        var targets = new[] { TargetLanguage.English, TargetLanguage.Vietnamese };

        var result = await _provider.TranslateAsync(chineseText, SourceLanguage.Auto, targets);

        result.Should().NotBeNull();
        result.OriginalText.Should().Be("你今天有时间吗？");
        result.Translations[TargetLanguage.English].Should().Be("Are you free today?");
        result.Translations[TargetLanguage.Vietnamese].Should().Be("Hôm nay bạn có rảnh không?");
    }
}
