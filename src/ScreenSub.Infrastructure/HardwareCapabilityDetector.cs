using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using ScreenSub.Core.Enums;
using ScreenSub.Core.Interfaces;
using ScreenSub.Core.Models;

namespace ScreenSub.Infrastructure;

public sealed class HardwareCapabilityDetector : IHardwareDetector
{
    private HardwareInfo? _cachedHardwareInfo;
    private readonly object _lock = new();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;

        public MEMORYSTATUSEX()
        {
            dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public HardwareInfo DetectHardware()
    {
        lock (_lock)
        {
            if (_cachedHardwareInfo != null)
            {
                return _cachedHardwareInfo;
            }

            _cachedHardwareInfo = DetectHardwareInternal();
            return _cachedHardwareInfo;
        }
    }

    private static HardwareInfo DetectHardwareInternal()
    {
        // 1. CPU Detection
        string cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown x64 Processor";
        int logicalCores = Environment.ProcessorCount;
        int physicalCores = Math.Max(1, logicalCores / 2);

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (var item in searcher.Get())
            {
                if (item["Name"] != null)
                {
                    cpuName = item["Name"].ToString()?.Trim() ?? cpuName;
                }
                if (item["NumberOfCores"] != null && int.TryParse(item["NumberOfCores"].ToString(), out int cores))
                {
                    physicalCores = cores;
                }
                if (item["NumberOfLogicalProcessors"] != null && int.TryParse(item["NumberOfLogicalProcessors"].ToString(), out int logCores))
                {
                    logicalCores = logCores;
                }
                break;
            }
        }
        catch
        {
            // Fallback to environment values
        }

        bool avx2 = Avx2.IsSupported;
        bool avx512 = Avx512F.IsSupported;

        // 2. RAM Detection
        long totalRam = 0;
        long availRam = 0;
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                totalRam = (long)memStatus.ullTotalPhys;
                availRam = (long)memStatus.ullAvailPhys;
            }
        }
        catch
        {
            totalRam = 8L * 1024 * 1024 * 1024; // Default fallback: 8GB
            availRam = 4L * 1024 * 1024 * 1024;
        }

        // 3. GPU Detection
        string gpuName = "None";
        string gpuVendor = "Unknown";
        string driverVersion = "Unknown";
        long totalVram = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM, DriverVersion FROM Win32_VideoController");
            var gpus = searcher.Get().Cast<ManagementObject>().ToList();

            // Prefer discrete GPU (NVIDIA, AMD) over integrated GPU (Intel/Microsoft)
            var discreteGpu = gpus.FirstOrDefault(g =>
            {
                string name = g["Name"]?.ToString() ?? "";
                return name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
            }) ?? gpus.FirstOrDefault();

            if (discreteGpu != null)
            {
                gpuName = discreteGpu["Name"]?.ToString()?.Trim() ?? "GPU";
                driverVersion = discreteGpu["DriverVersion"]?.ToString()?.Trim() ?? "Unknown";

                if (discreteGpu["AdapterRAM"] != null && long.TryParse(discreteGpu["AdapterRAM"].ToString(), out long vram))
                {
                    totalVram = vram;
                }

                if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase))
                {
                    gpuVendor = "NVIDIA";
                    // If WMI caps 32-bit AdapterRAM to 4GB or less, ensure at least 4GB for modern cards like GTX 1650 Ti
                    if (totalVram <= 0 && gpuName.Contains("1650"))
                    {
                        totalVram = 4L * 1024 * 1024 * 1024;
                    }
                }
                else if (gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    gpuVendor = "AMD";
                }
                else if (gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    gpuVendor = "Intel";
                }
            }
        }
        catch
        {
            gpuName = "Default Display Adapter";
        }

        // 4. Backends & Recommendations
        var supportedBackends = new List<InferenceBackendType> { InferenceBackendType.Cpu };
        var recommended = InferenceBackendType.Cpu;

        if (gpuVendor == "NVIDIA")
        {
            supportedBackends.Add(InferenceBackendType.DirectMl);
            supportedBackends.Add(InferenceBackendType.Cuda);
            recommended = InferenceBackendType.DirectMl; // DirectML is universally stable across Windows without requiring 3GB CUDA toolkit install
        }
        else if (gpuVendor == "AMD" || gpuVendor == "Intel")
        {
            supportedBackends.Add(InferenceBackendType.DirectMl);
            recommended = InferenceBackendType.DirectMl;
        }

        // 5. Free Disk Space
        long freeDisk = 0;
        try
        {
            var appDrive = new DriveInfo(Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory) ?? "C:\\");
            freeDisk = appDrive.AvailableFreeSpace;
        }
        catch
        {
            freeDisk = 50L * 1024 * 1024 * 1024;
        }

        return new HardwareInfo
        {
            CpuName = cpuName,
            PhysicalCores = physicalCores,
            LogicalProcessors = logicalCores,
            SupportsAvx2 = avx2,
            SupportsAvx512 = avx512,
            TotalRamBytes = totalRam,
            AvailableRamBytes = availRam,
            GpuName = gpuName,
            GpuVendor = gpuVendor,
            DriverVersion = driverVersion,
            TotalVramBytes = totalVram,
            AvailableVramBytes = totalVram,
            SupportedBackends = supportedBackends,
            RecommendedBackend = recommended,
            FreeDiskSpaceBytes = freeDisk
        };
    }
}

