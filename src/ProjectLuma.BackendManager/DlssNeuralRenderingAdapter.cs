using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.BackendManager;

/// <summary>Neural Rendering adapter targeting danielblnc/DLSS-NR-on-AMD with RDNA 2 guardrails.</summary>
public sealed class DlssNeuralRenderingAdapter : IRenderingBackendAdapter
{
    public string BackendId => "dlss-nr-on-amd";
    public RenderingFeatureType FeatureType => RenderingFeatureType.NeuralRendering;

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
            Features = { RenderingFeatureType.NeuralRendering }
        };

        if (!profile.EnableNeuralRendering ||
            profile.NeuralRenderingState is CapabilityState.Unsupported or CapabilityState.RequiresDriver)
        {
            return plan;
        }

        // Copy staged proxy/runtime files only if genuine runtime binaries are present (excluding setup.exe)
        bool hasStagedRuntime = false;
        if (!string.IsNullOrWhiteSpace(stagedBackendDirectory) && Directory.Exists(stagedBackendDirectory))
        {
            foreach (string file in Directory.GetFiles(stagedBackendDirectory, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name.StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase)) continue;
                hasStagedRuntime = true;
                plan.FilesToDeploy.Add(new BackendFileDeployItem
                {
                    SourcePath = file,
                    RelativeTargetPath = name.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("version.dll", StringComparison.OrdinalIgnoreCase)
                        ? profile.ProxyDllName
                        : name
                });
            }
        }

        if (!hasStagedRuntime)
        {
            plan.Notes.Add("DLSS-NR-on-AMD runtime binaries are managed through NeuralHub (handoff required; no game files staged for Neural Rendering).");
            return plan;
        }

        // Link/deploy user-imported nvngx_dlssnr.dll from local vault when runtime proxy is present
        var nrDll = userDlls.FirstOrDefault(d => d.DllKind.Equals("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
        if (nrDll != null)
        {
            plan.FilesToDeploy.Add(new BackendFileDeployItem
            {
                SourcePath = nrDll.StoredPath,
                RelativeTargetPath = "nvngx_dlssnr.dll",
                ExpectedSha256 = nrDll.Sha256
            });
        }

        // Configure dlssnr_on_amd.ini safely
        string qualityValue = profile.SelectedNeuralQuality switch
        {
            NeuralQualityMode.Superfast => "Superfast",
            NeuralQualityMode.Reference when profile.NeuralRenderingState != CapabilityState.Experimental => "Reference",
            _ => "Fast"
        };

        plan.FilesToDeploy.Add(new BackendFileDeployItem
        {
            RelativeTargetPath = "dlssnr_on_amd.ini",
            IsIniMerge = true,
            IniSections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["NeuralRendering"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["Enabled"] = "1",
                    ["QualityMode"] = qualityValue,
                    ["Multipass"] = profile.EnableNeuralMultipass ? "1" : "0"
                },
                ["Overlay"] = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["MenuKey"] = "End"
                }
            }
        });

        plan.Notes.Add($"Neural Rendering configured ({qualityValue} mode, overlay hotkey End).");
        return plan;
    }
}
