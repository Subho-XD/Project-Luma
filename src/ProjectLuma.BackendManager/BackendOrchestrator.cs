using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.BackendManager;

/// <summary>Coordinates all four independent rendering adapters and exposes hardware-filtered capability options.</summary>
public sealed class BackendOrchestrator : IBackendOrchestrator
{
    private readonly List<IRenderingBackendAdapter> _adapters;

    public IReadOnlyList<IRenderingBackendAdapter> Adapters => _adapters;

    public BackendOrchestrator()
    {
        _adapters = new List<IRenderingBackendAdapter>
        {
            new DlssNeuralRenderingAdapter(),
            new OptiScalerSuperResolutionAdapter(),
            new DlssRayReconstructionAdapter()
        };
    }

    public BackendInstallPlan ComposeCombinedPlan(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        IReadOnlyCollection<BackendManifest> manifests,
        IReadOnlyCollection<UserImportedDllRecord> userDlls)
    {
        var combined = new BackendInstallPlan
        {
            BackendId = profile.EnableNeuralRendering && !profile.EnableSuperResolution ? "dlss-nr-on-amd" : "optiscaler",
            BackendVersion = manifests.FirstOrDefault(m => m.Id == "optiscaler")?.CurrentInstalledVersion ?? "v0.9.5"
        };

        var deployedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in _adapters)
        {
            var manifest = manifests.FirstOrDefault(m =>
                m.Id.Equals(adapter.BackendId, StringComparison.OrdinalIgnoreCase) ||
                (adapter.BackendId == "dlss-rr-on-amd" && m.Id == "dlss-nr-on-amd"));

            string stagedDir = manifest?.StagedInstallPath ?? string.Empty;
            var subPlan = adapter.BuildInstallPlan(game, profile, stagedDir, userDlls);
            if (subPlan.FilesToDeploy.Count == 0) continue;

            foreach (var f in subPlan.Features)
                if (!combined.Features.Contains(f)) combined.Features.Add(f);

            combined.Notes.AddRange(subPlan.Notes);

            foreach (var item in subPlan.FilesToDeploy)
            {
                if (deployedTargets.Add(item.RelativeTargetPath))
                    combined.FilesToDeploy.Add(item);
            }
        }

        return combined;
    }

    public IReadOnlyList<UpscalingQualityPreset> GetSupportedPresets(string upscalerBackendId, string upscalerEngine)
    {
        // Only expose presets actually implemented by the active upscaler engine
        if (upscalerEngine.Contains("XeSS", StringComparison.OrdinalIgnoreCase))
        {
            return new[]
            {
                UpscalingQualityPreset.NativeAA,
                UpscalingQualityPreset.UltraQuality,
                UpscalingQualityPreset.Quality,
                UpscalingQualityPreset.Balanced,
                UpscalingQualityPreset.Performance,
                UpscalingQualityPreset.UltraPerformance
            };
        }

        return new[]
        {
            UpscalingQualityPreset.NativeAA,
            UpscalingQualityPreset.Quality,
            UpscalingQualityPreset.Balanced,
            UpscalingQualityPreset.Performance,
            UpscalingQualityPreset.UltraPerformance
        };
    }

    public IReadOnlyList<NeuralQualityMode> GetSupportedNeuralModes(GpuArchitecture architecture)
    {
        return architecture switch
        {
            GpuArchitecture.RDNA2 => new[] { NeuralQualityMode.Fast, NeuralQualityMode.Superfast }, // Reference disabled on RX 6000 per v0.9.0
            GpuArchitecture.RDNA3 => new[] { NeuralQualityMode.Reference, NeuralQualityMode.Fast, NeuralQualityMode.Superfast },
            GpuArchitecture.RDNA4 => new[] { NeuralQualityMode.Reference, NeuralQualityMode.Fast },
            _ => Array.Empty<NeuralQualityMode>()
        };
    }

    public IReadOnlyList<string> GetSupportedUpscalerEngines(GpuArchitecture architecture)
    {
        return architecture switch
        {
            GpuArchitecture.RDNA4 => new[] { "FSR 4.1.2 FP8 (FSR4-Upscale)", "FSR 3.1.2", "XeSS 2.0.2", "DLSS-SR 4.5 (DLSS-NR-on-AMD)" },
            GpuArchitecture.RDNA3 => new[] { "FSR 4.1.2 INT8 (FSR4-i8)", "FSR 3.1.2", "XeSS 2.0.2", "DLSS-SR 4.5 (DLSS-NR-on-AMD)" },
            GpuArchitecture.RDNA2 => new[] { "FSR 4.1.2 INT8 (FSR4-i8-2)", "FSR 3.1.2", "FSR 2.2.1", "XeSS 2.0.2" },
            GpuArchitecture.RDNA1 => new[] { "FSR 3.1.2", "FSR 2.2.1", "XeSS 2.0.2" },
            _ => new[] { "FSR 2.2.1" }
        };
    }

    public IReadOnlyList<(string Id, string Name, CapabilityState State, string Reason)> GetSupportedFrameGenEngines(
        GpuArchitecture architecture, GraphicsApi api)
    {
        var list = new List<(string, string, CapabilityState, string)>();
        bool isDx12 = api == GraphicsApi.DirectX12 || api == GraphicsApi.Unknown;

        list.Add(("fsr-fg", "AMD FSR Frame Generation 4.0.2",
            isDx12 && architecture >= GpuArchitecture.RDNA1 ? CapabilityState.Supported : CapabilityState.RequiresGameIntegration,
            isDx12 ? "Supported via OptiScaler FFX 2.4 SDK." : "Requires DirectX 12 game."));

        list.Add(("optifg", "OptiScaler OptiFG (DX12 + HUDfix)",
            isDx12 && architecture >= GpuArchitecture.RDNA1 ? CapabilityState.Supported : CapabilityState.RequiresGameIntegration,
            isDx12 ? "Experimental DX12 frame generation for non-FG games." : "OptiFG only supports DirectX 12."));

        list.Add(("nukem", "Nukem dlssg-to-fsr3 0.130",
            isDx12 && architecture >= GpuArchitecture.RDNA1 ? CapabilityState.SupportedWithLimitations : CapabilityState.RequiresGameIntegration,
            "Requires game with native DLSS Frame Generation inputs."));

        list.Add(("afmf2", "AMD Fluid Motion Frames 2 (Guided Adrenalin)",
            architecture >= GpuArchitecture.RDNA2 ? CapabilityState.SupportedWithLimitations : CapabilityState.Unsupported,
            architecture == GpuArchitecture.RDNA2
                ? "Configure in Adrenalin Gaming -> Graphics. Requires Exclusive Fullscreen + VSync OFF. Never combine with in-game FG."
                : "Configure in Adrenalin Gaming -> Graphics. Supports Borderless/Fullscreen."));

        list.Add(("dlss-fg", "DLSS Frame Generation (DLSS-NR-on-AMD)",
            architecture >= GpuArchitecture.RDNA3 ? CapabilityState.Supported : CapabilityState.Unsupported,
            architecture == GpuArchitecture.RDNA2
                ? "Explicitly unsupported on RX 6000 (RDNA 2) in DLSS-NR-on-AMD v0.8.0/v0.9.0."
                : "Requires RDNA 3/4 and user nvngx_dlssg.dll."));

        return list;
    }
}
