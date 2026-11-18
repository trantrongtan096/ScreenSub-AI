using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

public class MultiModelRouterUnitTests
{
    private static string ResolveWorkerScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "workers", "translation", "translation_worker.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate workers/translation/translation_worker.py.");
    }

    private static ISettingsService CreateSettingsMock(AppSettings settings)
    {
        var mock = new Mock<ISettingsService>();
        mock.Setup(s => s.Settings).Returns(settings);
        mock.Setup(s => s.SaveSettingsAsync()).Returns(Task.CompletedTask);
        return mock.Object;
    }

    [Fact]
    public void ModelRouting_DefaultDisabled_ShouldAlwaysSelectM2M100Baseline()
    {
        var settings = new AppSettings(); // Default EnableOfflineMultiModelRouter = false
        settings.EnableOfflineMultiModelRouter.Should().BeFalse();

        using var engine = new LocalNeuralTranslationEngine(CreateSettingsMock(settings));
        engine.EnableMultiModelRouter.Should().BeFalse();

        (string Src, string Tgt)[] pairs =
        [
            ("en", "vi"),
            ("vi", "en"),
            ("zh", "vi"),
            ("vi", "zh"),
            ("en", "zh"),
            ("zh", "en")
        ];

        foreach (var (src, tgt) in pairs)
        {
            engine.ResolvePreferredModelId(src, tgt).Should().Be(LocalNeuralTranslationEngine.ModelIdM2M100);
        }
    }

    [Theory]
    [InlineData("en", "vi", true, LocalNeuralTranslationEngine.ModelIdEnViT5, 1)]
    [InlineData("vi", "en", true, LocalNeuralTranslationEngine.ModelIdEnViT5, 1)]
    [InlineData("zh", "vi", true, LocalNeuralTranslationEngine.ModelIdOpusZhVi, 2)]
    [InlineData("zh-tw", "vi", true, LocalNeuralTranslationEngine.ModelIdOpusZhVi, 2)]
    [InlineData("zh-Hant", "vi", true, LocalNeuralTranslationEngine.ModelIdOpusZhVi, 2)]
    [InlineData("zh", "vi", false, LocalNeuralTranslationEngine.ModelIdM2M100, 2)]
    [InlineData("vi", "zh", true, LocalNeuralTranslationEngine.ModelIdM2M100, 2)]
    [InlineData("en", "zh", true, LocalNeuralTranslationEngine.ModelIdM2M100, 2)]
    [InlineData("zh", "en", true, LocalNeuralTranslationEngine.ModelIdM2M100, 2)]
    public void ModelRouting_WhenEnabled_ShouldRouteDeterministicallyByLanguagePair(
        string src,
        string tgt,
        bool experimentalZhVi,
        string expectedModelId,
        int expectedBeam)
    {
        var settings = new AppSettings
        {
            EnableOfflineMultiModelRouter = true,
            EnableExperimentalZhViOpusModel = experimentalZhVi
        };

        using var engine = new LocalNeuralTranslationEngine(CreateSettingsMock(settings));
        string modelId = engine.ResolvePreferredModelId(src, tgt);

        modelId.Should().Be(expectedModelId);
        engine.GetEffectiveBeamSize(modelId).Should().Be(expectedBeam);
    }

    [Fact]
    public void CacheIdentity_RouterToggleAndModelSwitches_ShouldPreventStaleCrossModelCacheHits()
    {
        var settings = new AppSettings
        {
            EnableOfflineMultiModelRouter = false,
            EnableExperimentalZhViOpusModel = true
        };

        using var engine = new LocalNeuralTranslationEngine(CreateSettingsMock(settings));

        string idBaselineEnVi = engine.GetCacheIdentity("en", "vi");

        settings.EnableOfflineMultiModelRouter = true;
        string idRouterEnVi = engine.GetCacheIdentity("en", "vi");
        string idRouterZhViExp = engine.GetCacheIdentity("zh", "vi");

        settings.EnableExperimentalZhViOpusModel = false;
        string idRouterZhViFallbackM2M = engine.GetCacheIdentity("zh", "vi");

        idRouterEnVi.Should().NotBe(idBaselineEnVi);
        idRouterZhViExp.Should().NotBe(idRouterZhViFallbackM2M);
        idRouterEnVi.Should().Contain(LocalNeuralTranslationEngine.ModelIdEnViT5);
        idRouterZhViExp.Should().Contain(LocalNeuralTranslationEngine.ModelIdOpusZhVi);
        idRouterZhViFallbackM2M.Should().Contain(LocalNeuralTranslationEngine.ModelIdM2M100);

        engine.BeamSize = 4;
        string idRouterEnViB4 = engine.GetCacheIdentity("en", "vi");
        idRouterEnViB4.Should().NotBe(idRouterEnVi);
    }

    [Fact]
    public async Task StrictOfflinePolicy_MissingAllOfflineModels_ShouldNeverFallBackToCloudOrMock()
    {
        string emptyModelsRoot = Path.Combine(Path.GetTempPath(), $"screensub_empty_models_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyModelsRoot);
        try
        {
            var settings = new AppSettings
            {
                TranslationEngineMode = TranslationEngineMode.Offline,
                AllowCloudFallbackOnOfflineFailure = false,
                EnableOfflineMultiModelRouter = true,
                AzureApiKey = "fake-key-should-never-be-called",
                AzureRegion = "eastus",
                UseMockTranslation = true
            };
            var settingsSvc = CreateSettingsMock(settings);

            using var engine = new LocalNeuralTranslationEngine(settingsSvc)
            {
                CustomModelsRoot = emptyModelsRoot,
                AllowLexicalFallback = false
            };

            var cache = new TranslationCache();
            using var localProvider = new LocalTranslationProvider(engine, cache, settingsSvc);
            var azureProvider = new AzureTranslatorProvider(new HttpClient(), settingsSvc, cache);
            var mockProvider = new MockTranslationProvider();
            var resolver = new TranslationProviderResolver(settingsSvc, localProvider, azureProvider, mockProvider);

            resolver.IsStrictOffline.Should().BeTrue();
            resolver.ResolveActiveProvider().Should().BeSameAs(localProvider);

            Func<Task> act = async () => await resolver.TranslateAsync(
                "Hello world",
                SourceLanguage.English,
                [TargetLanguage.Vietnamese]);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*MODEL NOT INSTALLED*");
        }
        finally
        {
            if (Directory.Exists(emptyModelsRoot))
            {
                Directory.Delete(emptyModelsRoot, true);
            }
        }
    }

    [Fact]
    public async Task PythonWorkerTokenizerAndLifecycleGuards_ShouldPreserveTradScriptStripPrefixAndEnforceLru()
    {
        string workerPath = ResolveWorkerScriptPath();
        string workerDir = Path.GetDirectoryName(workerPath)!;

        string pyCode = $@"
import sys, json
sys.path.insert(0, {JsonSerializer.Serialize(workerDir)})
import translation_worker as w

# 1. Trad/Simp Zh normalization check
trad = '太誇張了吧，排隊排了兩個小時還沒輪到我們，'
no_simp = w.preprocess_source_for_nmt(trad, 'zh', normalize_zh_trad_to_simp=False)
with_simp = w.preprocess_source_for_nmt(trad, 'zh', normalize_zh_trad_to_simp=True)

# 2. EnViT5 output prefix strip check
stripped_vi = w.refine_target_translation('vi: Chúng tôi chỉ đang uống chút gì đó.', 'vi', 'We are just having a drink.', model_family='envit5')
stripped_en = w.refine_target_translation('en: Hello there.', 'en', 'Xin chào.', model_family='envit5')

# 3. Inference guard blocks unload while _active_inference_count > 0
guard_triggered = False
w._active_inference_count = 1
try:
    w.unload_neural_model()
except RuntimeError:
    guard_triggered = True
finally:
    w._active_inference_count = 0

# 4. LRU eviction cap=1 check using lightweight mock entries
w._loaded_models.clear()
w._loaded_models['m1'] = {{'model_id': 'm1'}}
w._loaded_models['m2'] = {{'model_id': 'm2'}}
w._evict_if_needed('m3', max_loaded=1)
lru_after_cap1 = list(w._loaded_models.keys())

print(json.dumps({{
    'no_simp': no_simp,
    'with_simp': with_simp,
    'stripped_vi': stripped_vi,
    'stripped_en': stripped_en,
    'guard_triggered': guard_triggered,
    'lru_after_cap1': lru_after_cap1
}}, ensure_ascii=False))
";

        string tempScript = Path.Combine(Path.GetTempPath(), $"screensub_py_test_{Guid.NewGuid():N}.py");
        await File.WriteAllTextAsync(tempScript, pyCode, new System.Text.UTF8Encoding(false));

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{tempScript}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new System.Text.UTF8Encoding(false)
            };

            using var proc = Process.Start(psi)!;
            string stdout = await proc.StandardOutput.ReadToEndAsync();
            string stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(CancellationToken.None);

            proc.ExitCode.Should().Be(0, $"Python stderr: {stderr}");
            using var doc = JsonDocument.Parse(stdout.Trim());
            var root = doc.RootElement;

            string noSimp = root.GetProperty("no_simp").GetString()!;
            noSimp.Should().Be("太誇張了吧,排隊排了兩個小時還沒輪到我們");
            noSimp.Should().NotContain("夸张");

            root.GetProperty("stripped_vi").GetString().Should().Be("Chúng tôi chỉ đang uống chút gì đó.");
            root.GetProperty("stripped_en").GetString().Should().Be("Hello there.");
            root.GetProperty("guard_triggered").GetBoolean().Should().BeTrue();
            root.GetProperty("lru_after_cap1").GetArrayLength().Should().Be(0);
        }
        finally
        {
            if (File.Exists(tempScript))
            {
                File.Delete(tempScript);
            }
        }
    }
}
