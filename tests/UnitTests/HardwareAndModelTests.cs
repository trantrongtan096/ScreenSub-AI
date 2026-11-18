using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;
using ScreenSub.Infrastructure;
using Xunit;

namespace UnitTests;

public class HardwareAndModelTests
{
    [Fact]
    public void HardwareCapabilityDetector_ShouldDetectSystemHardware()
    {
        var detector = new HardwareCapabilityDetector();
        var info = detector.DetectHardware();

        info.Should().NotBeNull();
        info.CpuName.Should().NotBeNullOrWhiteSpace();
        info.PhysicalCores.Should().BeGreaterThan(0);
        info.LogicalProcessors.Should().BeGreaterThan(0);
        info.TotalRamBytes.Should().BeGreaterThan(0);
        info.FormattedRam.Should().Contain("GB");
        info.SupportedBackends.Should().Contain(InferenceBackendType.Cpu);
        info.FreeDiskSpaceBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LocalModelManager_ShouldManageLocalModels()
    {
        var mockSettings = new Mock<ISettingsService>();
        var tempDir = Path.Combine(Path.GetTempPath(), "ScreenSubAITests_" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings
        {
            ModelStorageDirectory = tempDir,
            ActiveTranslationModelId = "builtin-lexical-multilingual"
        };
        mockSettings.Setup(s => s.Settings).Returns(settings);

        var manager = new LocalModelManager(mockSettings.Object);

        var models = manager.GetModels();
        models.Should().NotBeEmpty();
        models.Should().Contain(m => m.Category == ModelCategory.Ocr);
        models.Should().Contain(m => m.Category == ModelCategory.Speech);
        models.Should().Contain(m => m.Category == ModelCategory.Translation);

        // Built-in Lexical should be marked Active by default
        var builtin = manager.GetModel("builtin-lexical-multilingual");
        builtin.Should().NotBeNull();
        builtin!.Status.Should().Be(ModelInstallationStatus.Active);

        // MarianMT without file should be NotInstalled
        var marian = manager.GetModel("marian-multilingual-int8");
        marian.Should().NotBeNull();
        marian!.Status.Should().Be(ModelInstallationStatus.NotInstalled);

        // Simulate placing a real model file in directory and verifying
        string targetFilePath = Path.Combine(tempDir, marian.LocalRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);
        await File.WriteAllBytesAsync(targetFilePath, new byte[1024]);

        bool verified = await manager.VerifyModelIntegrityAsync("marian-multilingual-int8");
        verified.Should().BeTrue();
        marian.Status.Should().Be(ModelInstallationStatus.Installed);

        // Clean up
        await manager.DeleteModelAsync("marian-multilingual-int8");
        if (Directory.Exists(settings.ModelStorageDirectory))
        {
            Directory.Delete(settings.ModelStorageDirectory, true);
        }
    }

    [Fact]
    public async Task LocalModelManager_VerifyModelIntegrity_ShouldRejectCorruptedWeightsOrMissingTokenizerForCt2Directory()
    {
        var mockSettings = new Mock<ISettingsService>();
        var tempDir = Path.Combine(Path.GetTempPath(), "ScreenSubAICt2Verify_" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { ModelStorageDirectory = tempDir };
        mockSettings.Setup(s => s.Settings).Returns(settings);

        var manager = new LocalModelManager(mockSettings.Object);
        var model = manager.GetModel("envit5_ct2_int8")!;
        string envit5Dir = Path.Combine(tempDir, "translation", "envit5_ct2_int8");
        Directory.CreateDirectory(envit5Dir);

        try
        {
            // Missing tokenizer -> false & Error
            await File.WriteAllBytesAsync(Path.Combine(envit5Dir, "model.bin"), new byte[] { 1, 2, 3 });
            bool missingTokOk = await manager.VerifyModelIntegrityAsync("envit5_ct2_int8");
            missingTokOk.Should().BeFalse();
            model.Status.Should().Be(ModelInstallationStatus.Error);

            // Tokenizers present but corrupted model.bin SHA-256 -> false & Error
            await File.WriteAllTextAsync(Path.Combine(envit5Dir, "tokenizer.json"), "{}");
            await File.WriteAllBytesAsync(Path.Combine(envit5Dir, "spiece.model"), new byte[] { 9, 9 });
            bool badHashOk = await manager.VerifyModelIntegrityAsync("envit5_ct2_int8");
            badHashOk.Should().BeFalse();
            model.Status.Should().Be(ModelInstallationStatus.Error);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void BetaEnViRouterProfile_ShouldKeepProductionDefaultOffAndConfigureControlledBeta()
    {
        var prodDefault = new AppSettings();
        prodDefault.EnableOfflineMultiModelRouter.Should().BeFalse();
        prodDefault.ActiveTranslationModelId.Should().Be("m2m100_418m_int8");
        prodDefault.MaxLoadedTranslationModelsInMemory.Should().Be(1);

        var betaHighRam = AppSettings.CreateBetaEnViRouterProfile(16L * 1024 * 1024 * 1024);
        betaHighRam.EnableOfflineMultiModelRouter.Should().BeTrue();
        betaHighRam.EnableExperimentalZhViOpusModel.Should().BeFalse();
        betaHighRam.AllowOfflineSpecializedFallbackToM2M100.Should().BeTrue();
        betaHighRam.MaxLoadedTranslationModelsInMemory.Should().Be(2);

        var betaLowRam = AppSettings.CreateBetaEnViRouterProfile(4L * 1024 * 1024 * 1024);
        betaLowRam.EnableOfflineMultiModelRouter.Should().BeTrue();
        betaLowRam.EnableExperimentalZhViOpusModel.Should().BeFalse();
        betaLowRam.MaxLoadedTranslationModelsInMemory.Should().Be(1);

        betaHighRam.ApplyLegacyM2M100RollbackProfile();
        betaHighRam.EnableOfflineMultiModelRouter.Should().BeFalse();
        betaHighRam.MaxLoadedTranslationModelsInMemory.Should().Be(1);
        betaHighRam.ActiveTranslationModelId.Should().Be("m2m100_418m_int8");
    }

    [Fact]
    public async Task SettingsService_OptInBetaEnvironmentVariable_ShouldLoadBetaProfileInMemoryOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ScreenSubAISettingsBeta_" + Guid.NewGuid().ToString("N"));
        string? prev = Environment.GetEnvironmentVariable("SCREENSUB_BETA_ROUTER");
        try
        {
            Environment.SetEnvironmentVariable("SCREENSUB_BETA_ROUTER", "1");
            var svc = new SettingsService(tempDir);
            await svc.LoadSettingsAsync();

            svc.Settings.EnableOfflineMultiModelRouter.Should().BeTrue();
            svc.Settings.EnableExperimentalZhViOpusModel.Should().BeFalse();
            svc.Settings.AllowOfflineSpecializedFallbackToM2M100.Should().BeTrue();
            svc.Settings.MaxLoadedTranslationModelsInMemory.Should().BeInRange(1, 2);

            Environment.SetEnvironmentVariable("SCREENSUB_BETA_ROUTER", null);
            var svcRollback = new SettingsService(tempDir);
            await svcRollback.LoadSettingsAsync();
            svcRollback.Settings.EnableOfflineMultiModelRouter.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCREENSUB_BETA_ROUTER", prev);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void Packaging_ThirdPartyLicensesAndInstallerScripts_ShouldMeetAttributionAndConsentRules()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ScreenSubAI.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull("repo root should be discoverable from unit test output directory");
        string root = dir!.FullName;

        string licensePath = Path.Combine(root, "docs", "THIRD_PARTY_LICENSES.txt");
        File.Exists(licensePath).Should().BeTrue();
        string licenseText = File.ReadAllText(licensePath);
        licenseText.Should().Contain("OpenRAIL-M");
        licenseText.Should().Contain("VietAI/envit5-translation");
        licenseText.Should().Contain("Attachment A");
        licenseText.Should().Contain("M2M-100");
        licenseText.Should().Contain("MIT");
        licenseText.Should().Contain("Apache-2.0");
        licenseText.Should().Contain("NLLB-200");
        licenseText.Should().Contain("CC BY-NC");

        string setupPyPath = Path.Combine(root, "installer", "setup_python_env.ps1");
        File.Exists(setupPyPath).Should().BeTrue();
        string setupPyText = File.ReadAllText(setupPyPath);
        setupPyText.Should().Contain("$VerifyOnly");
        setupPyText.Should().Contain("$ConsentToInstall");
        setupPyText.Should().Contain("Read-Host");

        string buildPsPath = Path.Combine(root, "installer", "build_installer.ps1");
        File.Exists(buildPsPath).Should().BeTrue();
        string buildPsText = File.ReadAllText(buildPsPath);
        buildPsText.Should().Contain("[bool]$SelfContained = $true");

        string issPath = Path.Combine(root, "installer", "ScreenSubAI_Setup.iss");
        File.Exists(issPath).Should().BeTrue();
        string issText = File.ReadAllText(issPath);
        issText.Should().Contain("THIRD_PARTY_LICENSES.txt");
        issText.Should().Contain("setup_python_env.ps1");
    }

    [Fact]
    public async Task PaddleOcrEngine_StageTelemetryProperties_ShouldInitializeCleanly()
    {
        await using var ocr = new ScreenSub.Ocr.PaddleOcrEngine();
        ocr.LastDecodeMs.Should().Be(0);
        ocr.LastDetMs.Should().Be(0);
        ocr.LastRecMs.Should().Be(0);
    }
}

