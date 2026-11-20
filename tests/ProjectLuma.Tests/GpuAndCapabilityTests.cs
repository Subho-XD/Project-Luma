using ProjectLuma.Compatibility;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Models;
using ProjectLuma.GpuDetection;
using ProjectLuma.Logging;
using Xunit;

namespace ProjectLuma.Tests;

public sealed class GpuAndCapabilityTests
{
    [Fact]
    public void PciDatabase_Classifies_All_RDNA_Generations_Accurately()
    {
        var db = new AmdGpuArchitectureDatabase();

        Assert.Equal(GpuArchitecture.RDNA1, db.Classify(0x1002, 0x731F).Architecture);
        Assert.Equal(GpuArchitecture.RDNA2, db.Classify(0x1002, 0x73FF).Architecture); // RX 6600
        Assert.Equal(GpuArchitecture.RDNA3, db.Classify(0x1002, 0x744C).Architecture); // RX 7900 XTX
        Assert.Equal(GpuArchitecture.RDNA4, db.Classify(0x1002, 0x7550).Architecture); // RX 9070 XT
        Assert.Equal(GpuArchitecture.Unknown, db.Classify(0x10DE, 0x2684).Architecture);
    }

    [Fact]
    public async Task LiveHardwareDetection_Finds_AMD_RX6600_With_64Bit_Vram()
    {
        var logger = new LumaLogger(Path.Combine(Path.GetTempPath(), "LumaTestLogs_" + Guid.NewGuid().ToString("N")[..6]));
        var detector = new WindowsGpuDetector(logger);

        var gpu = await detector.DetectPrimaryGpuAsync();

        Assert.Equal(0x1002u, gpu.VendorId);
        Assert.Equal(0x73FFu, gpu.DeviceId);
        Assert.Equal(GpuArchitecture.RDNA2, gpu.Architecture);
        Assert.True(gpu.VramBytes > 4_000_000_000UL, $"Expected 64-bit VRAM > 4GB, got {gpu.VramBytes}");
        Assert.True(gpu.MeetsAdrenalin2611Minimum, $"Expected Adrenalin 26.x+ driver detection, got {gpu.AdrenalinVersionDisplay}");
    }

    [Fact]
    public void CapabilityEngine_Rx6600_Reports_Honest_Neural_And_Gates_RayReconstruction_And_DlssFg()
    {
        var engine = new CapabilityEngine(NeuralHubProbeOptions.Disabled);
        var gpu = new DetectedGpuInfo
        {
            Manufacturer = "AMD",
            ModelName = "AMD Radeon RX 6600",
            VendorId = 0x1002,
            DeviceId = 0x73FF,
            Architecture = GpuArchitecture.RDNA2,
            VramBytes = 8541929472UL,
            DriverVersion = "32.0.21045.29003",
            AdrenalinVersionDisplay = "26.11.2",
            MeetsAdrenalin2611Minimum = true,
            DirectX12Supported = true,
            HipRuntimeInstalled = true,
            HipRuntimeDllName = "amdhip64_6.dll",
            Hip72SdkAvailable = false
        };

        // Without HIP 7.2 and nvngx_dlssnr.dll -> Neural Rendering is RequiresDependency
        var profileMissing = engine.EvaluateHardware(gpu, Array.Empty<UserImportedDllRecord>(), Array.Empty<BackendManifest>(), UpdateChannel.Stable);
        Assert.Equal(CapabilityState.RequiresDependency, profileMissing.Evaluations[RenderingFeatureType.NeuralRendering].State);
        Assert.Equal(CapabilityState.Supported, profileMissing.Evaluations[RenderingFeatureType.SuperResolution].State);
        Assert.Equal(CapabilityState.Supported, profileMissing.Evaluations[RenderingFeatureType.FrameGeneration].State);
        Assert.Equal(CapabilityState.Unsupported, profileMissing.Evaluations[RenderingFeatureType.RayReconstruction].State);

        // With HIP 7.2 and imported nvngx_dlssnr.dll -> Neural Rendering transitions to Experimental (RDNA 2)
        gpu.Hip72SdkAvailable = true;
        gpu.HipRuntimeDllName = "amdhip64_7.dll";
        string fakeDllPath = Path.GetTempFileName();
        try
        {
            var dlls = new[]
            {
                new UserImportedDllRecord { FileName = "nvngx_dlssnr.dll", DllKind = "nvngx_dlssnr", FileVersion = "310.9.16.0", StoredPath = fakeDllPath }
            };
            var profileReady = engine.EvaluateHardware(gpu, dlls, Array.Empty<BackendManifest>(), UpdateChannel.Stable);
            Assert.Equal(CapabilityState.Experimental, profileReady.Evaluations[RenderingFeatureType.NeuralRendering].State);
            Assert.True(profileReady.Evaluations[RenderingFeatureType.NeuralRendering].CanEnable);
        }
        finally
        {
            if (File.Exists(fakeDllPath)) File.Delete(fakeDllPath);
        }
    }
}
