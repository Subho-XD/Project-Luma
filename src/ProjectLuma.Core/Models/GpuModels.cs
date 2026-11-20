using System.Text.Json.Serialization;
using ProjectLuma.Core.Enums;

namespace ProjectLuma.Core.Models;

/// <summary>Hardware specification and runtime environment detected from DXGI, WMI, Registry, and system probes.</summary>
public sealed class DetectedGpuInfo
{
    public string Manufacturer { get; set; } = "Unknown";
    public string ModelName { get; set; } = "Unknown GPU";
    public uint VendorId { get; set; }
    public uint DeviceId { get; set; }
    public uint SubsystemId { get; set; }
    public uint RevisionId { get; set; }
    public string PciIdentifier => $"VEN_{VendorId:X4}&DEV_{DeviceId:X4}";
    public GpuArchitecture Architecture { get; set; } = GpuArchitecture.Unknown;
    public string Codename { get; set; } = "Unknown";
    public string GpuTarget { get; set; } = string.Empty;
    public bool IsIntegrated { get; set; }
    public ulong VramBytes { get; set; }
    public double VramGigabytes => Math.Round(VramBytes / (1024d * 1024d * 1024d), 2);
    public string VramFormatted => VramBytes > 0 ? $"{VramGigabytes:0.##} GB" : "Shared";
    public string DriverVersion { get; set; } = "Unknown";
    public string AdrenalinVersionDisplay { get; set; } = "Unknown";
    public bool MeetsAdrenalin2611Minimum { get; set; }
    public bool DirectX12Supported { get; set; }
    public string DirectXFeatureLevel { get; set; } = "Unknown";
    public bool VulkanSupported { get; set; }
    public bool HipRuntimeInstalled { get; set; }
    public string HipRuntimeDllName { get; set; } = string.Empty;
    public string HipRuntimeVersion { get; set; } = string.Empty;
    public string HipRuntimePath { get; set; } = string.Empty;
    public bool Hip72SdkAvailable { get; set; }
    public bool Hip72OfficiallySupported { get; set; }
    public bool HipRuntimeInitialized { get; set; }
    public int HipRuntimeApiVersion { get; set; }
    public int HipDeviceCount { get; set; }
    public string HipInitDiagnostic { get; set; } = string.Empty;
    public string CpuName { get; set; } = "Unknown";
    public int CpuCores { get; set; }
    public int CpuLogicalProcessors { get; set; }
    public bool Avx2Supported { get; set; }
    public string OsCaption { get; set; } = "Windows";
    public string OsVersion { get; set; } = string.Empty;
    public int OsBuildNumber { get; set; }
    public bool IsWindows11 => OsBuildNumber >= 22000;
    public DateTime DetectedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Capability verdict for a single rendering feature on the active GPU/game/dependency state.</summary>
public sealed class FeatureCapabilityEvaluation
{
    public RenderingFeatureType Feature { get; set; }
    public CapabilityState State { get; set; }
    public string StatusBadgeText { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string DetailedReason { get; set; } = string.Empty;
    public string RecommendedBackendId { get; set; } = string.Empty;
    public string RecommendedBackendName { get; set; } = string.Empty;
    public List<string> MissingPrerequisites { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool CanEnable { get; set; }
    public bool IsExperimental => State == CapabilityState.Experimental || State == CapabilityState.SupportedWithLimitations;
}

/// <summary>Serializable hardware capability profile matching Section 4 of the specification.</summary>
public sealed class HardwareCapabilityProfile
{
    [JsonPropertyName("gpu")]
    public string Gpu => GpuInfo.ModelName;

    [JsonPropertyName("pci_id")]
    public string PciId => GpuInfo.PciIdentifier;

    [JsonPropertyName("architecture")]
    public string Architecture => GpuInfo.Architecture.ToString().ToUpperInvariant();

    [JsonPropertyName("gpu_target")]
    public string GpuTarget => GpuInfo.GpuTarget;

    [JsonPropertyName("hip72_official_support")]
    public bool Hip72OfficiallySupported => GpuInfo.Hip72OfficiallySupported;

    [JsonPropertyName("vram")]
    public string Vram => GpuInfo.VramFormatted;

    [JsonPropertyName("driver_version")]
    public string DriverVersion => GpuInfo.DriverVersion;

    [JsonPropertyName("hip_runtime")]
    public string HipRuntime => GpuInfo.Hip72SdkAvailable
        ? $"HIP 7.2 ({GpuInfo.HipRuntimeVersion})"
        : GpuInfo.HipRuntimeInstalled
            ? $"{GpuInfo.HipRuntimeDllName} ({GpuInfo.HipRuntimeVersion}) - HIP 7.2 missing"
            : "Not detected";

    [JsonPropertyName("capabilities")]
    public Dictionary<string, string> CapabilitiesSummary { get; set; } = new();

    [JsonIgnore]
    public DetectedGpuInfo GpuInfo { get; set; } = new();

    [JsonIgnore]
    public Dictionary<RenderingFeatureType, FeatureCapabilityEvaluation> Evaluations { get; set; } = new();
}

/// <summary>Entry in data/gpu_profiles/amd_gpus.json.</summary>
public sealed class GpuCatalogEntry
{
    public string DeviceId { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Codename { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string ComputeTier { get; set; } = string.Empty;
    public int DefaultVramGb { get; set; }
    public bool IsIntegrated { get; set; }
    public bool SupportsAfmf2Borderless { get; set; }
    public bool SupportsHwFlipMetering { get; set; }
}
