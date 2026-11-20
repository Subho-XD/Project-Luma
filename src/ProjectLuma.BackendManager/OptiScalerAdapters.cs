using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.BackendManager;

/// <summary>OptiScaler Super Resolution &amp; Frame Generation adapters with FFX 2.4 / FSR 4.1.2 INT8 RDNA 2 support.</summary>
public sealed class OptiScalerSuperResolutionAdapter : IRenderingBackendAdapter
{
    public string BackendId => "optiscaler";
    public RenderingFeatureType FeatureType => RenderingFeatureType.SuperResolution;

    public BackendInstallPlan BuildInstallPlan(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        string stagedBackendDirectory,
        IReadOnlyCollection<UserImportedDllRecord> userDlls)
    {
        var plan = new BackendInstallPlan
        {
            BackendId = BackendId,
            BackendVersion = profile.TargetBackendVersion,
            Features = { RenderingFeatureType.SuperResolution }
        };

        if (!profile.EnableSuperResolution && !profile.EnableFrameGeneration)
            return plan;

        if (!string.IsNullOrWhiteSpace(stagedBackendDirectory) && Directory.Exists(stagedBackendDirectory))
        {
            foreach (string file in Directory.GetFiles(stagedBackendDirectory, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.Equals("luma-package-meta.json", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = Path.GetRelativePath(stagedBackendDirectory, file);
                if (name.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase))
                    rel = profile.ProxyDllName;

                if (name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
                    continue; // Managed via non-destructive INI merge below

                plan.FilesToDeploy.Add(new BackendFileDeployItem
                {
                    SourcePath = file,
                    RelativeTargetPath = rel
                });
            }
        }

        string upscalerValue = MapUpscalerIniValue(profile.SelectedUpscalerEngine);
        string fsr4PresetIndex = profile.SelectedUpscalingPreset switch
        {
            UpscalingQualityPreset.NativeAA => "0",
            UpscalingQualityPreset.UltraQuality or UpscalingQualityPreset.Quality => "1",
            UpscalingQualityPreset.Balanced => "2",
            UpscalingQualityPreset.Performance => "3",
            UpscalingQualityPreset.UltraPerformance => "5",
            _ => "auto"
        };
        string int8Mode = profile.SelectedUpscalerEngine.Contains("i8-2", StringComparison.OrdinalIgnoreCase)
            ? "2"
            : (profile.SelectedUpscalerEngine.Contains("INT8", StringComparison.OrdinalIgnoreCase) ? "1" : "0");
        bool isFsr4Engine = profile.SelectedUpscalerEngine.Contains("FSR 4", StringComparison.OrdinalIgnoreCase);

        var qualityOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["QualityRatioOverrideEnabled"] = profile.SelectedUpscalingPreset == UpscalingQualityPreset.UltraQuality ? "true" : "auto"
        };
        if (profile.SelectedUpscalingPreset == UpscalingQualityPreset.NativeAA) qualityOverrides["QualityRatioDLAA"] = "1.0";
        else if (profile.SelectedUpscalingPreset == UpscalingQualityPreset.UltraQuality) qualityOverrides["QualityRatioUltraQuality"] = "1.3";
        else if (profile.SelectedUpscalingPreset == UpscalingQualityPreset.Quality) qualityOverrides["QualityRatioQuality"] = "1.5";
        else if (profile.SelectedUpscalingPreset == UpscalingQualityPreset.Balanced) qualityOverrides["QualityRatioBalanced"] = "1.7";
        else if (profile.SelectedUpscalingPreset == UpscalingQualityPreset.Performance) qualityOverrides["QualityRatioPerformance"] = "2.0";
        else if (profile.SelectedUpscalingPreset == UpscalingQualityPreset.UltraPerformance) qualityOverrides["QualityRatioUltraPerformance"] = "3.0";

        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Upscalers"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Dx12Upscaler"] = game.GraphicsApi == GraphicsApi.DirectX12 || game.GraphicsApi == GraphicsApi.Unknown ? upscalerValue : "auto",
                ["Dx11Upscaler"] = game.GraphicsApi == GraphicsApi.DirectX11 ? (upscalerValue == "xess" ? "xess_12" : "fsr31") : "auto",
                ["VulkanUpscaler"] = game.GraphicsApi == GraphicsApi.Vulkan ? upscalerValue : "auto"
            },
            ["FSR"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Fsr4Update"] = isFsr4Engine ? "true" : "auto",
                ["Fsr4ForceEnableInt8"] = int8Mode,
                ["Fsr4Preset"] = fsr4PresetIndex,
                ["Fsr4EnableWatermark"] = profile.ShowFsr4Watermark ? "true" : "false"
            },
            ["Sharpness"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["OverrideSharpness"] = "true",
                ["Sharpness"] = profile.Sharpness.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            },
            ["QualityOverrides"] = qualityOverrides,
            ["Hotkeys"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["ShortcutKey"] = "0x2D" // Insert
            },
            ["ProjectLuma"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Profile"] = profile.SelectedPreset.ToString(),
                ["UpscalerBackend"] = profile.SelectedUpscalerBackend,
                ["UpscalerEngine"] = profile.SelectedUpscalerEngine,
                ["QualityPreset"] = profile.SelectedUpscalingPreset.ToString()
            }
        };

        // Frame Generation sub-section integrated cleanly when enabled without double-FG
        if (profile.EnableFrameGeneration && !profile.SelectedFrameGenEngine.Contains("AFMF", StringComparison.OrdinalIgnoreCase))
        {
            plan.Features.Add(RenderingFeatureType.FrameGeneration);
            bool isNukem = profile.SelectedFrameGenEngine.Contains("Nukem", StringComparison.OrdinalIgnoreCase);
            bool isFsrFg = profile.SelectedFrameGenEngine.Contains("FSR", StringComparison.OrdinalIgnoreCase);

            sections["FrameGen"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Enabled"] = "true",
                ["FGInput"] = isNukem ? "dlssg" : (game.NativeFrameGenSupported ? "fsrfg" : "upscaler"),
                ["FGOutput"] = isFsrFg || !isNukem ? "fsrfg" : "dlssg",
                ["FGNvngxReplacement"] = isNukem ? "Nukems" : "FFX",
                ["DrawUIOverFG"] = profile.EnableHudFix ? "true" : "false"
            };
        }

        plan.FilesToDeploy.Add(new BackendFileDeployItem
        {
            RelativeTargetPath = "OptiScaler.ini",
            IsIniMerge = true,
            IniSections = sections
        });

        plan.Notes.Add($"OptiScaler Super Resolution configured ({profile.SelectedUpscalerEngine}, {profile.SelectedUpscalingPreset}, Proxy={profile.ProxyDllName}).");
        return plan;
    }

    private static string MapUpscalerIniValue(string engine)
    {
        if (engine.Contains("XeSS", StringComparison.OrdinalIgnoreCase)) return "xess";
        if (engine.Contains("DLSS", StringComparison.OrdinalIgnoreCase)) return "dlss";
        if (engine.Contains("2.2", StringComparison.OrdinalIgnoreCase)) return "fsr22";
        return "ffx"; // FSR 2.3 / 3.1 / 4.x via FFX 2.4 SDK
    }
}

/// <summary>Dedicated Ray Reconstruction adapter (DLSS-RR Model F on RDNA 3/4; strictly gated on RDNA 2).</summary>
public sealed class DlssRayReconstructionAdapter : IRenderingBackendAdapter
{
    public string BackendId => "dlss-rr-on-amd";
    public RenderingFeatureType FeatureType => RenderingFeatureType.RayReconstruction;

    public BackendInstallPlan BuildInstallPlan(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        string stagedBackendDirectory,
        IReadOnlyCollection<UserImportedDllRecord> userDlls)
    {
        var plan = new BackendInstallPlan
        {
            BackendId = BackendId,
            BackendVersion = profile.TargetBackendVersion,
            Features = { RenderingFeatureType.RayReconstruction }
        };

        if (!profile.EnableRayReconstruction || profile.RayReconstructionState != CapabilityState.Supported)
            return plan;

        var rrDll = userDlls.FirstOrDefault(d => d.DllKind.Equals("nvngx_dlssd", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
        if (rrDll != null)
        {
            plan.FilesToDeploy.Add(new BackendFileDeployItem
            {
                SourcePath = rrDll.StoredPath,
                RelativeTargetPath = "nvngx_dlssd.dll",
                ExpectedSha256 = rrDll.Sha256
            });
        }

        return plan;
    }
}
