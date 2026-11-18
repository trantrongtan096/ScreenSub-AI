using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Models;
using ScreenSub.Translation;
using Xunit;

namespace UnitTests;

public class TranslationCacheIdentityAndWorkerIpcTests
{
    [Fact]
    public void Cache_DifferentProviderOrModelOrDecodingIdentity_ShouldNotCollide()
    {
        var cache = new TranslationCache();
        var targets = new[] { TargetLanguage.Vietnamese };

        var localResult = new TranslationResult
        {
            OriginalText = "Let's go!",
            DetectedLanguage = "en",
            Translations = new Dictionary<TargetLanguage, string>
            {
                [TargetLanguage.Vietnamese] = "Đi thôi! (Local B2)"
            },
            ProviderName = "Local AI"
        };

        string idLocalB2 = "LocalNeural|M2M-100-418M|fp=773934e4cbd4|dec=beam=2,lp=1,rp=1.05,ng=3,v2-general";
        string idLocalB4 = "LocalNeural|M2M-100-418M|fp=773934e4cbd4|dec=beam=4,lp=1,rp=1.05,ng=3,v2-general";
        string idAzure = "AzureTranslator|api=3.0|post=v2-general";
        string idGemini = "Gemini|model=gemini-2.5-flash|post=v2-general";

        cache.Set("Let's go!", SourceLanguage.English, targets, localResult, cacheIdentity: idLocalB2);

        cache.TryGet("Let's go!", SourceLanguage.English, targets, out var hitB2, cacheIdentity: idLocalB2).Should().BeTrue();
        hitB2!.Translations[TargetLanguage.Vietnamese].Should().Be("Đi thôi! (Local B2)");
        hitB2.IsFromCache.Should().BeTrue();

        // Changing decoding config, model fingerprint, or provider MUST miss rather than leaking stale entries
        cache.TryGet("Let's go!", SourceLanguage.English, targets, out _, cacheIdentity: idLocalB4).Should().BeFalse();
        cache.TryGet("Let's go!", SourceLanguage.English, targets, out _, cacheIdentity: idAzure).Should().BeFalse();
        cache.TryGet("Let's go!", SourceLanguage.English, targets, out _, cacheIdentity: idGemini).Should().BeFalse();
    }

    [Fact]
    public void Cache_ErrorOrLexicalPlaceholders_ShouldNeverBeStored()
    {
        var cache = new TranslationCache();
        var targets = new[] { TargetLanguage.Vietnamese };
        string identity = "LocalNeural|M2M-100-418M|fp=test";

        string[] blockedValues =
        [
            "[MODEL NOT INSTALLED: missing]",
            "[LEXICAL — LIMITED VOCABULARY] test",
            "[Azure chưa cấu hình]",
            "[Gemini Timeout]",
            "[Lỗi dịch: IPC crashed]",
            "   "
        ];

        foreach (var bad in blockedValues)
        {
            cache.Set("Sample", SourceLanguage.English, targets, new TranslationResult
            {
                OriginalText = "Sample",
                Translations = new Dictionary<TargetLanguage, string> { [TargetLanguage.Vietnamese] = bad }
            }, cacheIdentity: identity);
        }

        cache.TryGet("Sample", SourceLanguage.English, targets, out _, cacheIdentity: identity).Should().BeFalse();
        cache.Count.Should().Be(0);
    }

    [Fact]
    public async Task ScenarioH_WorkerCrashAndRespawn_ShouldRecoverWithoutMockFallback()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"screensub_ipc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string markerFile = Path.Combine(tempDir, "crashed_once.flag");
        string scriptPath = Path.Combine(tempDir, "fake_crash_worker.py");

        // On first translate attempt -> exit(1) (simulated crash). On respawn -> succeed.
        string py = $@"
import sys, json, os
marker = {System.Text.Json.JsonSerializer.Serialize(markerFile)}
while True:
    line = sys.stdin.readline()
    if not line:
        break
    req = json.loads(line)
    cmd = req.get('cmd')
    rid = req.get('id', 0)
    if cmd == 'ping':
        print(json.dumps({{'id': rid, 'status': 'ok', 'pong': True, 'model_loaded': True, 'model_name': 'FakeTestModel', 'model_fingerprint': 'fake01'}}), flush=True)
    elif cmd == 'translate':
        if not os.path.exists(marker):
            open(marker, 'w').write('1')
            sys.exit(1)
        print(json.dumps({{'id': rid, 'status': 'ok', 'translated_text': 'Recovered OK', 'tokenize_ms': 1.0, 'infer_ms': 5.0, 'postproc_ms': 0.5}}), flush=True)
    elif cmd == 'quit':
        break
";
        await File.WriteAllTextAsync(scriptPath, py);

        try
        {
            using var engine = new LocalNeuralTranslationEngine("python", scriptPath);
            engine.AllowLexicalFallback = false;

            await engine.InitializeAsync();
            engine.IsNeuralModelLoaded.Should().BeTrue();
            engine.ModelFingerprint.Should().Be("fake01");

            // Should recover transparently on automatic single retry after crash!
            string output = await engine.TranslateTextAsync("Hello", "en", "vi");
            output.Should().Be("Recovered OK");
            output.Should().NotContain("[LEXICAL");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScenarioH_PersistentCrashAndMalformedJson_ShouldSurfaceErrorNotMock()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"screensub_ipc_bad_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string scriptPath = Path.Combine(tempDir, "fake_malformed_worker.py");

        string py = @"
import sys, json
while True:
    line = sys.stdin.readline()
    if not line:
        break
    req = json.loads(line)
    cmd = req.get('cmd')
    rid = req.get('id', 0)
    if cmd == 'ping':
        print(json.dumps({'id': rid, 'status': 'ok', 'pong': True, 'model_loaded': True, 'model_name': 'FakeBad', 'model_fingerprint': 'bad01'}), flush=True)
    elif cmd == 'translate':
        sys.stdout.write('{corrupt_json_without_end\n')
        sys.stdout.flush()
    elif cmd == 'quit':
        break
";
        await File.WriteAllTextAsync(scriptPath, py);

        try
        {
            using var engine = new LocalNeuralTranslationEngine("python", scriptPath);
            engine.AllowLexicalFallback = false;
            await engine.InitializeAsync();

            Func<Task> act = async () => await engine.TranslateTextAsync("Hello", "en", "vi");
            await act.Should().ThrowAsync<InvalidOperationException>();
            engine.WorkerCrashOrResetCount.Should().BeGreaterThan(0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScenarioH_WorkerTimeout_ShouldResetAndRecover()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"screensub_ipc_to_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string markerFile = Path.Combine(tempDir, "hang_once.flag");
        string scriptPath = Path.Combine(tempDir, "fake_timeout_worker.py");

        string py = $@"
import sys, json, time, os
marker = {System.Text.Json.JsonSerializer.Serialize(markerFile)}
while True:
    line = sys.stdin.readline()
    if not line:
        break
    req = json.loads(line)
    cmd = req.get('cmd')
    rid = req.get('id', 0)
    if cmd == 'ping':
        print(json.dumps({{'id': rid, 'status': 'ok', 'pong': True, 'model_loaded': True, 'model_name': 'FakeTO', 'model_fingerprint': 'to01'}}), flush=True)
    elif cmd == 'translate':
        if not os.path.exists(marker):
            open(marker, 'w').write('1')
            time.sleep(2.5)
        print(json.dumps({{'id': rid, 'status': 'ok', 'translated_text': 'Fast After Timeout'}}), flush=True)
    elif cmd == 'quit':
        break
";
        await File.WriteAllTextAsync(scriptPath, py);

        try
        {
            using var engine = new LocalNeuralTranslationEngine("python", scriptPath)
            {
                RequestTimeoutMs = 250,
                AllowLexicalFallback = false
            };
            await engine.InitializeAsync();

            // First call hangs 2.5s (> 250ms), triggers reset once, retries -> succeeds cleanly without hang or mock!
            string output = await engine.TranslateTextAsync("Hello", "en", "vi");
            output.Should().Be("Fast After Timeout");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScenarioH_MidFlightCancellation_ShouldNotDesynchronizeStdoutIds()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"screensub_ipc_sync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string scriptPath = Path.Combine(tempDir, "fake_slow_worker.py");

        string py = @"
import sys, json, time
while True:
    line = sys.stdin.readline()
    if not line:
        break
    req = json.loads(line)
    cmd = req.get('cmd')
    rid = req.get('id', 0)
    if cmd == 'ping':
        print(json.dumps({'id': rid, 'status': 'ok', 'pong': True, 'model_loaded': True, 'model_name': 'FakeSync', 'model_fingerprint': 'sync01'}), flush=True)
    elif cmd == 'translate':
        txt = req.get('text', '')
        if txt == 'FIRST':
            time.sleep(0.25)
        print(json.dumps({'id': rid, 'status': 'ok', 'translated_text': f'OUT:{txt}'}), flush=True)
    elif cmd == 'quit':
        break
";
        await File.WriteAllTextAsync(scriptPath, py);

        try
        {
            using var engine = new LocalNeuralTranslationEngine("python", scriptPath);
            await engine.InitializeAsync();

            using var cts1 = new CancellationTokenSource(60);
            Func<Task> act1 = async () => await engine.TranslateTextAsync("FIRST", "en", "vi", cts1.Token);
            await act1.Should().ThrowAsync<OperationCanceledException>();

            // Engine state must remain Ready (not Error) and next request MUST receive OUT:SECOND, never OUT:FIRST!
            engine.State.Should().Be(ModelRuntimeState.Ready);
            string second = await engine.TranslateTextAsync("SECOND", "en", "vi");
            second.Should().Be("OUT:SECOND");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
