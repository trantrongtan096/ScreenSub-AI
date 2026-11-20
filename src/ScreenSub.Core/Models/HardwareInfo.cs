using ScreenSub.Core.Enums;

namespace ScreenSub.Core.Models;

public sealed class HardwareInfo
{
    public string CpuName { get; init; } = "Unknown CPU";
    public int PhysicalCores { get; init; } = 4;
    public int LogicalProcessors { get; init; } = 4;
    public bool SupportsAvx2 { get; init; } = true;
    public bool SupportsAvx512 { get; init; } = false;

    public long TotalRamBytes { get; init; }
    public long AvailableRamBytes { get; init; }

    public string GpuName { get; init; } = "None";
    public string GpuVendor { get; init; } = "Unknown";
    public string DriverVersion { get; init; } = "Unknown";
    public long TotalVramBytes { get; init; }
    public long AvailableVramBytes { get; init; }

    public List<InferenceBackendType> SupportedBackends { get; init; } = [InferenceBackendType.Cpu];
    public InferenceBackendType RecommendedBackend { get; init; } = InferenceBackendType.Cpu;

    public long FreeDiskSpaceBytes { get; init; }

    public string FormattedRam => $"{TotalRamBytes / (1024.0 * 1024 * 1024):0.#} GB";
    public string FormattedVram => TotalVramBytes > 0 ? $"{TotalVramBytes / (1024.0 * 1024 * 1024):0.#} GB" : "N/A";
    public string FormattedDiskSpace => $"{FreeDiskSpaceBytes / (1024.0 * 1024 * 1024):0.#} GB khả dụng";
}
