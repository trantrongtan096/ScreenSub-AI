using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Models;

public sealed class ModelMetadata
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "1.0.0";
    public ModelCategory Category { get; init; } = ModelCategory.Translation;
    public string SupportedLanguages { get; init; } = "EN, ZH, VI";
    public long SizeBytes { get; init; }
    public ModelInstallationStatus Status { get; set; } = ModelInstallationStatus.NotInstalled;
    public string QuantizationType { get; init; } = "INT8";
    public string BackendCompatibility { get; init; } = "CPU, DirectML, CUDA";
    public string License { get; init; } = "Apache-2.0 / MIT";
    public string SourceUrl { get; init; } = string.Empty;
    public string LocalRelativePath { get; init; } = string.Empty;
    public string Sha256Checksum { get; init; } = string.Empty;
    public double DownloadProgressPercent { get; set; } = 0.0;
    public DateTime? LastVerified { get; set; }

    public string FormattedSize => $"{SizeBytes / (1024.0 * 1024):0.#} MB";
    public bool IsInstalled => Status is ModelInstallationStatus.Installed or ModelInstallationStatus.Active;
}
