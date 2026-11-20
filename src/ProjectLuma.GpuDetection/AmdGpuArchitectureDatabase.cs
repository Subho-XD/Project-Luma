using System.Text.Json;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Models;

namespace ProjectLuma.GpuDetection;

/// <summary>Hardware PCI Vendor/Device ID database classifying AMD Radeon architectures.</summary>
public sealed class AmdGpuArchitectureDatabase
{
    private readonly Dictionary<string, GpuCatalogEntry> _byDeviceId = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GpuCatalogEntry> _entries = new();

    private sealed class CatalogRoot
    {
        public List<GpuCatalogEntry> AmdDeviceCatalog { get; set; } = new();
    }

    public AmdGpuArchitectureDatabase(string? catalogPath = null)
    {
        string resolvedPath = ResolveCatalogPath(catalogPath);
        if (File.Exists(resolvedPath))
        {
            string json = File.ReadAllText(resolvedPath);
            var root = JsonSerializer.Deserialize<CatalogRoot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (root?.AmdDeviceCatalog != null)
            {
                foreach (var entry in root.AmdDeviceCatalog)
                {
                    _entries.Add(entry);
                    _byDeviceId[entry.DeviceId] = entry;
                }
            }
        }
    }

    public IReadOnlyList<GpuCatalogEntry> Entries => _entries;

    public (GpuArchitecture Architecture, GpuCatalogEntry? Entry) Classify(uint vendorId, uint deviceId)
    {
        if (vendorId != 0x1002) return (GpuArchitecture.Unknown, null);

        string devHex = deviceId.ToString("X4");
        if (_byDeviceId.TryGetValue(devHex, out var exact) &&
            Enum.TryParse<GpuArchitecture>(exact.Architecture, ignoreCase: true, out var parsed))
        {
            return (parsed, exact);
        }

        // Architectural PCI Device ID family heuristics for unlisted revisions/SKUs
        GpuArchitecture inferred = deviceId switch
        {
            >= 0x7500 and <= 0x75FF => GpuArchitecture.RDNA4,
            >= 0x7440 and <= 0x749F => GpuArchitecture.RDNA3,
            >= 0x73A0 and <= 0x743F => GpuArchitecture.RDNA2,
            >= 0x7310 and <= 0x736F => GpuArchitecture.RDNA1,
            >= 0x7600 and <= 0x79FF => GpuArchitecture.FutureRDNA,
            >= 0x6700 and <= 0x69FF => GpuArchitecture.GCN,
            _ => GpuArchitecture.Unknown
        };

        return (inferred, null);
    }

    private static string ResolveCatalogPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;

        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "data", "gpu_profiles", "amd_gpus.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "gpu_profiles", "amd_gpus.json")),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "gpu_profiles", "amd_gpus.json")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
