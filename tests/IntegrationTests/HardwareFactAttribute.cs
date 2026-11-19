using System;
using System.Collections.Generic;
using System.IO;
using ScreenSub.Audio.Capture;
using ScreenSub.Speech.Whisper;
using ScreenSub.Translation;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// Custom xUnit Fact attribute for real hardware & local model E2E tests.
/// Instead of silently returning inside a test body and reporting a false PASS when hardware/models
/// are unavailable, this attribute marks the test as SKIPPED with an explicit diagnostic reason.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HardwareFactAttribute : FactAttribute
{
    public const string EdgeExecutablePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    public HardwareFactAttribute(
        bool requiresEdge = false,
        bool requiresWhisperBase = false,
        bool requiresTranslationModel = false,
        bool requiresAudioOutput = false,
        string? customExecutablePathRequirement = null)
    {
        var missing = new List<string>();

        if (requiresEdge && !File.Exists(EdgeExecutablePath))
        {
            missing.Add($"Microsoft Edge ({EdgeExecutablePath})");
        }

        if (!string.IsNullOrWhiteSpace(customExecutablePathRequirement) && !File.Exists(customExecutablePathRequirement))
        {
            missing.Add($"Required binary ({customExecutablePathRequirement})");
        }

        if (requiresWhisperBase && !WhisperModelManager.IsModelAvailable("base"))
        {
            missing.Add("Whisper Base model (ggml-base.bin)");
        }

        if (requiresTranslationModel && !LocalNeuralTranslationEngine.IsDefaultModelInstalledOnDisk())
        {
            missing.Add("Offline Neural Translation models (m2m100_418m_ct2 / envit5_ct2_int8)");
        }

        if (requiresAudioOutput)
        {
            try
            {
                using var wasapi = new WasapiAudioCaptureService();
                if (wasapi.GetOutputDevices().Count == 0)
                {
                    missing.Add("Active Windows WASAPI output audio endpoint");
                }
            }
            catch (Exception ex)
            {
                missing.Add($"WASAPI audio probe failed ({ex.Message})");
            }
        }

        if (missing.Count > 0)
        {
            Skip = $"SKIPPED — Missing hardware/model prerequisite(s): {string.Join("; ", missing)}";
        }
    }
}
