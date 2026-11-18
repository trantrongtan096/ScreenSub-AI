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
        string tempDir = Path.Combine(Path.GetTempPath(), "ScreenSub_Test_" + Guid.NewGuid());
        var service = new SettingsService(tempDir);
        service.Settings.AzureRegion = "southeastasia";
        service.Settings.UseMockTranslation = true;

        service.SetSecret("TestIntegrationKey", "test-val-999");
        await service.SaveSettingsAsync();

        var loadedService = new SettingsService(tempDir);
        await loadedService.LoadSettingsAsync();

        loadedService.GetSecret("TestIntegrationKey").Should().Be("test-val-999");
        loadedService.Settings.AzureRegion.Should().Be("southeastasia");

        try { Directory.Delete(tempDir, true); } catch { }
    }
}

public class SubtitleCoordinatorTests
{
    [Fact]
    public void Coordinator_InitialState_ShouldBeCleanWithoutSampleSubtitles()
    {
        var settings = new SettingsService();
        var capture = new ScreenSub.Capture.ScreenCaptureService();
        var changeDetector = new ScreenSub.Capture.FrameChangeDetector();
        var ocr = new ScreenSub.Ocr.PaddleOcrEngine();
        var cache = new TranslationCache();
        var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settings, cache);
        var mock = new MockTranslationProvider();

        var coordinator = new ScreenSub.Desktop.Services.SubtitleCoordinator(
            capture, ocr, azure, mock, settings, changeDetector);

        coordinator.CurrentSubtitle.HasContent.Should().BeFalse();
        coordinator.AverageLatencyMs.Should().Be(0);
        coordinator.State.Should().Be(SystemState.Idle);
    }

    [Fact]
    public void Coordinator_Stop_ShouldResetSubtitlesAndLatency()
    {
        var settings = new SettingsService();
        var capture = new ScreenSub.Capture.ScreenCaptureService();
        var changeDetector = new ScreenSub.Capture.FrameChangeDetector();
        var ocr = new ScreenSub.Ocr.PaddleOcrEngine();
        var cache = new TranslationCache();
        var azure = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settings, cache);
        var mock = new MockTranslationProvider();

        var coordinator = new ScreenSub.Desktop.Services.SubtitleCoordinator(
            capture, ocr, azure, mock, settings, changeDetector);

        coordinator.Start();
        coordinator.Stop();

        coordinator.CurrentSubtitle.HasContent.Should().BeFalse();
        coordinator.AverageLatencyMs.Should().Be(0);
        coordinator.State.Should().BeOneOf(SystemState.Idle, SystemState.Stopped);
    }
}

public class AzureTranslatorValidationTests
{
    [Fact]
    public async Task TranslateAsync_WithoutKey_ShouldThrowWithClearMessage()
    {
        var settings = new SettingsService();
        settings.Settings.AzureApiKey = string.Empty;
        var cache = new TranslationCache();
        var provider = new AzureTranslatorProvider(new System.Net.Http.HttpClient(), settings, cache);

        var act = async () => await provider.TranslateAsync("Hello", SourceLanguage.English, [TargetLanguage.Vietnamese]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Chưa cấu hình Azure Translator API Key*");
        provider.StatusMessage.Should().Be("Chưa cấu hình API Key");
    }
}

public class HotkeyConfigurationTests
{
    [Fact]
    public void AppSettings_DefaultHotkey_ShouldBeCtrlAltQ()
    {
        var settings = new ScreenSub.Core.Models.AppSettings();
        settings.HotkeyDisplayText.Should().Be("Ctrl + Alt + Q");
        settings.HotkeyModifiers.Should().Be(3); // Control (2) | Alt (1)
        settings.HotkeyKey.Should().Be(60); // Key.Q
    }

    [Fact]
    public async Task SettingsService_ShouldPersistCustomHotkey()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "ScreenSub_HotkeyTest_" + Guid.NewGuid());
        var service = new SettingsService(tempDir);
        service.Settings.HotkeyDisplayText = "Ctrl + Shift + F8";
        service.Settings.HotkeyModifiers = 6; // Control (2) | Shift (4)
        service.Settings.HotkeyKey = 97; // Key.F8
        await service.SaveSettingsAsync();

        var loaded = new SettingsService(tempDir);
        await loaded.LoadSettingsAsync();
        loaded.Settings.HotkeyDisplayText.Should().Be("Ctrl + Shift + F8");
        loaded.Settings.HotkeyModifiers.Should().Be(6);
        loaded.Settings.HotkeyKey.Should().Be(97);

        try { Directory.Delete(tempDir, true); } catch { }
    }
}

