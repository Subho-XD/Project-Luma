using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.GpuDetection;

/// <summary>Detects active GPU, PCI IDs, architecture, 64-bit VRAM, WDDM/Adrenalin drivers, HIP runtimes, and CPU/OS readiness.</summary>
public sealed class WindowsGpuDetector : IGpuDetector
{
    private readonly AmdGpuArchitectureDatabase _db;
    private readonly ILumaLogger _logger;

    public WindowsGpuDetector(ILumaLogger logger, string? gpuCatalogPath = null)
    {
        _logger = logger;
        _db = new AmdGpuArchitectureDatabase(gpuCatalogPath);
    }

    public IReadOnlyList<GpuCatalogEntry> GetCatalogEntries() => _db.Entries;

    public Task<DetectedGpuInfo> DetectPrimaryGpuAsync(CancellationToken ct = default)
    {
        return Task.Run(() => DetectInternal(), ct);
    }

    private DetectedGpuInfo DetectInternal()
    {
        var info = new DetectedGpuInfo
        {
            DetectedAtUtc = DateTime.UtcNow,
            Avx2Supported = Avx2.IsSupported,
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsBuildNumber = Environment.OSVersion.Version.Build
        };

        // 1. Enumerate via DXGI COM API first (accurate 64-bit VRAM & PCI IDs), preferring discrete AMD GPUs in hybrid systems
        var dxgiAdapters = DxgiGpuEnumerator.EnumerateHardwareAdapters();
        var primary = SelectBestGpuAdapter(dxgiAdapters, _db);

        if (primary != null)
        {
            info.ModelName = primary.Description;
            info.VendorId = primary.VendorId;
            info.DeviceId = primary.DeviceId;
            info.SubsystemId = primary.SubSysId;
            info.RevisionId = primary.Revision;
            info.VramBytes = primary.DedicatedVideoMemoryBytes;
            info.Manufacturer = primary.VendorId switch
            {
                0x1002 => "AMD",
                0x10DE => "NVIDIA",
                0x8086 => "Intel",
                _ => $"PCI_0x{primary.VendorId:X4}"
            };
        }

        // 2. Classify GPU Architecture from PCI Device ID database
        var (arch, entry) = _db.Classify(info.VendorId, info.DeviceId);
        info.Architecture = arch;
        if (entry != null)
        {
            info.Codename = entry.Codename;
            info.IsIntegrated = entry.IsIntegrated;
        }
        info.GpuTarget = ResolveGpuTarget(info.VendorId, info.DeviceId, info.Architecture, info.Codename);
        // Per AMD HIP SDK 7.2 Windows docs, RDNA 3 / RDNA 4 workstation/desktop targets are officially listed; gfx1032 (RX 6600) / RDNA 2 is excluded from official HIP 7.2 support.
        info.Hip72OfficiallySupported = info.VendorId == 0x1002 && info.Architecture >= GpuArchitecture.RDNA3;

        // 3. Enrich from Display Class Registry (WDDM DriverVersion, Adrenalin ReleaseVersion, 64-bit qwMemorySize)
        ReadDisplayRegistryMetadata(info);

        // 4. Enrich CPU, OS caption, and fallback GPU fields from WMI
        ReadWmiMetadata(info);

        // 5. Probe DirectX 12, Vulkan, and AMD HIP runtimes
        ProbeGraphicsAndComputeRuntimes(info);

        _logger.Info("GpuDetection",
            $"Detected {info.ModelName} ({info.PciIdentifier}, {info.Architecture}, {info.VramFormatted}, Driver {info.DriverVersion})",
            $"HIP={(info.HipRuntimeInstalled ? $"{info.HipRuntimeDllName} v{info.HipRuntimeVersion}" : "None")}, AVX2={info.Avx2Supported}, Build={info.OsBuildNumber}");

        return info;
    }

    private static void ReadDisplayRegistryMetadata(DetectedGpuInfo info)
    {
        const string displayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(displayClassKey);
            if (root == null) return;

            string targetMatch = info.VendorId > 0 ? $"VEN_{info.VendorId:X4}&DEV_{info.DeviceId:X4}" : "VEN_1002";

            foreach (string subName in root.GetSubKeyNames())
            {
                if (!Regex.IsMatch(subName, @"^\d{4}$")) continue;
                using var sub = root.OpenSubKey(subName);
                if (sub == null) continue;

                string? matchingId = sub.GetValue("MatchingDeviceId") as string;
                if (string.IsNullOrWhiteSpace(matchingId)) continue;
                if (!matchingId.Contains(targetMatch, StringComparison.OrdinalIgnoreCase)) continue;

                if (info.ModelName == "Unknown GPU" && sub.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
                    info.ModelName = desc.Trim();

                if (sub.GetValue("DriverVersion") is string drv && !string.IsNullOrWhiteSpace(drv))
                    info.DriverVersion = drv.Trim();

                if (sub.GetValue("RadeonSoftwareVersion") is string rsw && !string.IsNullOrWhiteSpace(rsw))
                    info.AdrenalinVersionDisplay = rsw.Trim();
                else if (sub.GetValue("ReleaseVersion") is string rel && !string.IsNullOrWhiteSpace(rel))
                    info.AdrenalinVersionDisplay = rel.Trim();

                if (info.VramBytes == 0 && sub.GetValue("HardwareInformation.qwMemorySize") is long qwMem && qwMem > 0)
                    info.VramBytes = (ulong)qwMem;
                else if (info.VramBytes == 0 && sub.GetValue("HardwareInformation.qwMemorySize") is byte[] bytes && bytes.Length >= 8)
                    info.VramBytes = BitConverter.ToUInt64(bytes, 0);

                info.MeetsAdrenalin2611Minimum = CheckMeetsMinimumDriver(info.DriverVersion, info.AdrenalinVersionDisplay);
                break;
            }
        }
        catch
        {
            // Registry read failure handled by WMI fallback
        }
    }

    public static string ResolveGpuTarget(uint vendorId, uint deviceId, GpuArchitecture arch, string codename)
    {
        if (vendorId != 0x1002) return "non-amd";
        return deviceId switch
        {
            0x73FF or 0x73FE or 0x73EF => "gfx1032", // Navi 23 (RX 6600 / 6650 XT)
            0x73DF => "gfx1031", // Navi 22 (RX 6700 XT)
            0x73BF or 0x73AF => "gfx1030", // Navi 21 (RX 6800 / 6900 XT)
            0x731F or 0x7340 => "gfx1010", // RDNA 1
            0x744C or 0x7480 => "gfx1100", // RDNA 3
            0x7550 => "gfx1201", // RDNA 4
            _ => arch switch
            {
                GpuArchitecture.RDNA4 => "gfx120x",
                GpuArchitecture.RDNA3 => "gfx110x",
                GpuArchitecture.RDNA2 when codename.Contains("23", StringComparison.OrdinalIgnoreCase) => "gfx1032",
                GpuArchitecture.RDNA2 => "gfx103x",
                GpuArchitecture.RDNA1 => "gfx101x",
                _ => "unknown"
            }
        };
    }

    public static DxgiGpuEnumerator.DxgiAdapterRecord? SelectBestGpuAdapter(
        IReadOnlyList<DxgiGpuEnumerator.DxgiAdapterRecord> adapters,
        AmdGpuArchitectureDatabase db)
    {
        return adapters
            .OrderByDescending(a =>
            {
                if (a.VendorId != 0x1002) return 0;
                var (_, entry) = db.Classify(a.VendorId, a.DeviceId);
                return entry is { IsIntegrated: true } ? 50 : 100;
            })
            .ThenByDescending(a => a.DedicatedVideoMemoryBytes)
            .FirstOrDefault();
    }

    public static bool CheckMeetsMinimumDriver(string wddmVersion, string adrenalinVersion)
    {
        // WDDM 32.0.210xx+ corresponds to Adrenalin 25.x / 26.x on Windows 11.
        // Also parse Adrenalin YY.M.R if available.
        if (!string.IsNullOrWhiteSpace(adrenalinVersion))
        {
            var m = Regex.Match(adrenalinVersion, @"^(\d+)\.(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int year) && int.TryParse(m.Groups[2].Value, out int month))
            {
                if (year > 26 || (year == 26 && month >= 1)) return true;
                if (year < 26) return false;
            }
        }

        if (Version.TryParse(wddmVersion, out var parsed))
        {
            // WDDM 32.0.21000+ is Adrenalin 25/26 era; 32.0.1xxxx is 24.x; < 32.0 is older
            return parsed.Major >= 32 && (parsed.Build >= 21000 || parsed.Revision >= 21000);
        }

        return false;
    }

    private static void ReadWmiMetadata(DetectedGpuInfo info)
    {
        try
        {
            using var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController");
            foreach (ManagementObject obj in gpuSearcher.Get())
            {
                string? pnp = obj["PNPDeviceID"]?.ToString();
                if (info.VendorId == 0 && pnp != null && pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
                {
                    var match = Regex.Match(pnp, @"VEN_([0-9A-Fa-f]{4})&DEV_([0-9A-Fa-f]{4})");
                    if (match.Success)
                    {
                        info.VendorId = Convert.ToUInt32(match.Groups[1].Value, 16);
                        info.DeviceId = Convert.ToUInt32(match.Groups[2].Value, 16);
                        info.Manufacturer = "AMD";
                    }
                    info.ModelName = obj["Name"]?.ToString() ?? info.ModelName;
                }

                if (info.DriverVersion == "Unknown" && pnp != null &&
                    pnp.Contains(info.PciIdentifier, StringComparison.OrdinalIgnoreCase))
                {
                    info.DriverVersion = obj["DriverVersion"]?.ToString() ?? info.DriverVersion;
                }
            }
        }
        catch
        {
            // Ignore WMI query errors
        }

        try
        {
            using var cpuSearcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject obj in cpuSearcher.Get())
            {
                info.CpuName = obj["Name"]?.ToString()?.Trim() ?? info.CpuName;
                info.CpuCores = Convert.ToInt32(obj["NumberOfCores"] ?? 0);
                info.CpuLogicalProcessors = Convert.ToInt32(obj["NumberOfLogicalProcessors"] ?? 0);
                break;
            }
        }
        catch
        {
            info.CpuLogicalProcessors = Environment.ProcessorCount;
        }

        try
        {
            using var osSearcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in osSearcher.Get())
            {
                info.OsCaption = obj["Caption"]?.ToString()?.Trim() ?? info.OsCaption;
                info.OsVersion = obj["Version"]?.ToString()?.Trim() ?? info.OsVersion;
                if (int.TryParse(obj["BuildNumber"]?.ToString(), out int build) && build > 0)
                    info.OsBuildNumber = build;
                break;
            }
        }
        catch
        {
            // Keep Environment.OSVersion fallback
        }
    }

    private static void ProbeGraphicsAndComputeRuntimes(DetectedGpuInfo info)
    {
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        info.DirectX12Supported = File.Exists(Path.Combine(sys32, "d3d12.dll"));
        info.DirectXFeatureLevel = info.DirectX12Supported
            ? info.Architecture >= GpuArchitecture.RDNA2 ? "12_2 (Ultimate)" : "12_1"
            : "11_0";
        info.VulkanSupported = File.Exists(Path.Combine(sys32, "vulkan-1.dll"));

        // Probe HIP 7.2 (amdhip64_7.dll) first across System32 and AMD ROCm SDK directories
        var hipCandidates = new List<string>
        {
            Path.Combine(sys32, "amdhip64_7.dll"),
            Path.Combine(sys32, "amdhip64_6.dll")
        };

        string rocmRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AMD", "ROCm");
        if (Directory.Exists(rocmRoot))
        {
            foreach (string verDir in Directory.GetDirectories(rocmRoot).OrderByDescending(d => d))
            {
                string binDir = Path.Combine(verDir, "bin");
                if (!Directory.Exists(binDir)) continue;
                hipCandidates.AddRange(Directory.GetFiles(binDir, "amdhip64_*.dll").OrderByDescending(f => f));
            }
        }

        string? hip7 = hipCandidates.FirstOrDefault(p => Path.GetFileName(p).StartsWith("amdhip64_7", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        string? hip6 = hipCandidates.FirstOrDefault(p => Path.GetFileName(p).StartsWith("amdhip64_6", StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        string? chosen = hip7 ?? hip6 ?? hipCandidates.FirstOrDefault(File.Exists);

        if (chosen != null)
        {
            info.HipRuntimeInstalled = true;
            info.HipRuntimePath = chosen;
            info.HipRuntimeDllName = Path.GetFileName(chosen);
            info.Hip72SdkAvailable = hip7 != null;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(chosen);
                info.HipRuntimeVersion = !string.IsNullOrWhiteSpace(vi.ProductVersion) ? vi.ProductVersion : (vi.FileVersion ?? "Detected");
            }
            catch
            {
                info.HipRuntimeVersion = "Detected";
            }

            ProbeHipInitialization(chosen, info);
        }
        else
        {
            info.HipRuntimeInstalled = false;
            info.HipInitDiagnostic = "No amdhip64 runtime DLL found in System32 or AMD ROCm directories.";
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HipGetIntDelegate(out int value);

    public static void ProbeHipInitialization(string dllPath, DetectedGpuInfo info)
    {
        if (!NativeLibrary.TryLoad(dllPath, out IntPtr handle))
        {
            info.HipRuntimeInitialized = false;
            info.HipInitDiagnostic = $"Failed to load {Path.GetFileName(dllPath)} via NativeLibrary.";
            return;
        }

        try
        {
            int apiVer = 0;
            int devCount = 0;
            int rcVer = -1;
            int rcDev = -1;

            if (NativeLibrary.TryGetExport(handle, "hipRuntimeGetVersion", out IntPtr pVer))
            {
                var fnVer = Marshal.GetDelegateForFunctionPointer<HipGetIntDelegate>(pVer);
                rcVer = fnVer(out apiVer);
            }

            if (NativeLibrary.TryGetExport(handle, "hipGetDeviceCount", out IntPtr pCnt))
            {
                var fnCnt = Marshal.GetDelegateForFunctionPointer<HipGetIntDelegate>(pCnt);
                rcDev = fnCnt(out devCount);
            }

            info.HipRuntimeApiVersion = apiVer;
            info.HipDeviceCount = devCount;
            info.HipRuntimeInitialized = rcVer == 0 && rcDev == 0 && devCount > 0;
            info.HipInitDiagnostic = info.HipRuntimeInitialized
                ? $"Initialized {Path.GetFileName(dllPath)} cleanly (HIP API {apiVer}, {devCount} HIP device(s) visible)."
                : $"HIP runtime probe returned rcVer={rcVer}, rcDev={rcDev}, devices={devCount}.";
        }
        catch (Exception ex)
        {
            info.HipRuntimeInitialized = false;
            info.HipInitDiagnostic = $"HIP probe exception: {ex.Message}";
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }
}
