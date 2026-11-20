using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using ProjectLuma.Core.Services;

namespace ProjectLuma.Compatibility;

/// <summary>Evaluates hardware and per-game capabilities independently across all four rendering pillars.</summary>
public sealed class CapabilityEngine : ICapabilityEngine
{
    private readonly NeuralHubProbeOptions? _neuralHubProbeOptions;

    public CapabilityEngine(NeuralHubProbeOptions? neuralHubProbeOptions = null)
    {
        _neuralHubProbeOptions = neuralHubProbeOptions;
    }

    public HardwareCapabilityProfile EvaluateHardware(
        DetectedGpuInfo gpu,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        IReadOnlyCollection<BackendManifest> manifests,
        UpdateChannel channel)
    {
        var nrEval = EvaluateNeuralRendering(gpu, null, userDlls, channel, _neuralHubProbeOptions);
        var srEval = EvaluateSuperResolution(gpu, null, userDlls, channel);
        var fgEval = EvaluateFrameGeneration(gpu, null, userDlls, channel);
        var rrEval = EvaluateRayReconstruction(gpu, null, userDlls, channel);

        var profile = new HardwareCapabilityProfile
        {
            GpuInfo = gpu,
            Evaluations = new Dictionary<RenderingFeatureType, FeatureCapabilityEvaluation>
            {
                [RenderingFeatureType.NeuralRendering] = nrEval,
                [RenderingFeatureType.SuperResolution] = srEval,
                [RenderingFeatureType.FrameGeneration] = fgEval,
                [RenderingFeatureType.RayReconstruction] = rrEval
            },
            CapabilitiesSummary = new Dictionary<string, string>
            {
                ["neural_rendering"] = FormatSummarySlug(nrEval.State),
                ["super_resolution"] = FormatSummarySlug(srEval.State),
                ["frame_generation"] = FormatSummarySlug(fgEval.State),
                ["ray_reconstruction"] = FormatSummarySlug(rrEval.State),
                ["amd_frame_generation"] = gpu.Architecture >= GpuArchitecture.RDNA2 ? "driver_dependent" : "unsupported"
            }
        };

        return profile;
    }

    public GameEnhancementProfile EvaluateGame(
        DiscoveredGame game,
        DetectedGpuInfo gpu,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        IReadOnlyCollection<BackendManifest> manifests,
        CompatibilitySeedProfile? seedProfile,
        UpdateChannel channel)
    {
        var nrEval = EvaluateNeuralRendering(gpu, game, userDlls, channel, _neuralHubProbeOptions);
        var srEval = EvaluateSuperResolution(gpu, game, userDlls, channel);
        var fgEval = EvaluateFrameGeneration(gpu, game, userDlls, channel);
        var rrEval = EvaluateRayReconstruction(gpu, game, userDlls, channel);

        var profile = new GameEnhancementProfile
        {
            GameId = game.Id,
            GameTitle = game.Title,
            NeuralRenderingState = nrEval.State,
            SuperResolutionState = srEval.State,
            FrameGenerationState = fgEval.State,
            RayReconstructionState = rrEval.State,
            NeuralRenderingReason = nrEval.DetailedReason,
            SuperResolutionReason = srEval.DetailedReason,
            FrameGenerationReason = fgEval.DetailedReason,
            RayReconstructionReason = rrEval.DetailedReason,
            EnableNeuralRendering = nrEval.State is CapabilityState.Supported or CapabilityState.Experimental,
            SelectedNeuralQuality = gpu.Architecture == GpuArchitecture.RDNA2 ? NeuralQualityMode.Fast : NeuralQualityMode.Fast,
            EnableSuperResolution = srEval.CanEnable,
            SelectedUpscalerBackend = "optiscaler",
            SelectedUpscalerEngine = gpu.Architecture switch
            {
                GpuArchitecture.RDNA4 => "FSR 4.1.2 FP8 (FSR4-Upscale)",
                GpuArchitecture.RDNA3 => "FSR 4.1.2 INT8 (FSR4-i8)",
                GpuArchitecture.RDNA2 => "FSR 4.1.2 INT8 (FSR4-i8-2)",
                _ => "FSR 3.1.2"
            },
            SelectedUpscalingPreset = UpscalingQualityPreset.Quality,
            EnableFrameGeneration = false,
            SelectedFrameGenEngine = game.GraphicsApi is GraphicsApi.DirectX11 or GraphicsApi.Vulkan
                ? "AMD Fluid Motion Frames 2 (Guided Adrenalin)"
                : (game.NativeFrameGenSupported ? "AMD FSR Frame Generation 4.0.2" : "OptiScaler OptiFG (DX12 + HUDfix)"),
            EnableRayReconstruction = rrEval.State == CapabilityState.Supported && game.NativeRayReconstructionSupported,
            ProxyDllName = game.ImportsVersionDll ? "version.dll" : "dxgi.dll",
            MinimumDriverVersion = seedProfile?.MinimumDriverVersion ?? "26.1.1",
            TargetBackendVersion = manifests.FirstOrDefault(m => m.Id == "optiscaler")?.PinnedStableVersion ?? "v0.9.5",
            GraphicsApiRequirements = game.GraphicsApi == GraphicsApi.Unknown ? (seedProfile?.RequiredApi ?? "DirectX 12") : game.GraphicsApi.ToString(),
            AntiCheatRestrictions = game.AntiCheatStatus switch
            {
                AntiCheatStatus.DetectedBlocked => $"Blocked: {game.AntiCheatDetails}",
                AntiCheatStatus.DetectedOfflineAllowed => $"Offline Only: {game.AntiCheatDetails}",
                AntiCheatStatus.WindowsAppsProtected => "Protected WindowsApps directory",
                _ => "None detected"
            },
            LastVerificationDate = seedProfile?.LastVerificationDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd")
        };

        if (game.NativeFrameGenSupported)
        {
            profile.DoubleFgGuardTriggered = true;
            profile.DoubleFgWarning = "Your game already supports Frame Generation. Project Luma has disabled duplicate injection to avoid visual artifacts and frame-pacing conflicts.";
            profile.NativeFrameGenGuide = "Open this game's Graphics settings and enable its built-in Frame Generation option (FSR or DLSS Frame Generation). Keep Project Luma OptiFG and driver AFMF 2 off while native Frame Generation is active.";
        }

        if (seedProfile != null)
        {
            profile.KnownProblems.AddRange(seedProfile.KnownProblems);
            profile.RequiredDlls.AddRange(seedProfile.RequiredDlls);
            foreach (var kv in seedProfile.RecommendedSettings)
                profile.RequiredSettings[kv.Key] = kv.Value;

            if (seedProfile.RecommendedSettings.TryGetValue("ProxyName", out var proxy))
                profile.ProxyDllName = proxy;

            if (seedProfile.TestedOnRx6600 && gpu.Architecture == GpuArchitecture.RDNA2)
                profile.TestedCombinations.Add("RX 6600 (RDNA 2) + DX12 + OptiScaler / DLSS-NR-on-AMD");
        }
        else
        {
            profile.UntestedCombinations.Add($"{gpu.Architecture} + {game.GraphicsApi} + OptiScaler v0.9.5");
        }

        ApplyPreset(profile, EnhancementProfilePreset.Balanced, game, gpu, userDlls);
        PopulateFriendlyStatuses(game, profile);
        UpdateNeuralActivationDiagnostics(game, profile, gpu, userDlls, manifests, _neuralHubProbeOptions);

        return profile;
    }

    public GameEnhancementProfile ReevaluatePreservingUserSettings(
        GameEnhancementProfile existing,
        DiscoveredGame game,
        DetectedGpuInfo gpu,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        IReadOnlyCollection<BackendManifest> manifests,
        CompatibilitySeedProfile? seedProfile,
        UpdateChannel channel)
    {
        var nrEval = EvaluateNeuralRendering(gpu, game, userDlls, channel, _neuralHubProbeOptions);
        var srEval = EvaluateSuperResolution(gpu, game, userDlls, channel);
        var fgEval = EvaluateFrameGeneration(gpu, game, userDlls, channel);
        var rrEval = EvaluateRayReconstruction(gpu, game, userDlls, channel);

        existing.GameId = game.Id;
        existing.GameTitle = game.Title;
        existing.NeuralRenderingState = nrEval.State;
        existing.SuperResolutionState = srEval.State;
        existing.FrameGenerationState = fgEval.State;
        existing.RayReconstructionState = rrEval.State;
        existing.NeuralRenderingReason = nrEval.DetailedReason;
        existing.SuperResolutionReason = srEval.DetailedReason;
        existing.FrameGenerationReason = fgEval.DetailedReason;
        existing.RayReconstructionReason = rrEval.DetailedReason;
        existing.TargetBackendVersion = manifests.FirstOrDefault(m => m.Id == "optiscaler")?.PinnedStableVersion ?? existing.TargetBackendVersion;
        existing.GraphicsApiRequirements = game.GraphicsApi == GraphicsApi.Unknown ? (seedProfile?.RequiredApi ?? "DirectX 12") : game.GraphicsApi.ToString();

        bool canSr = srEval.CanEnable;
        bool canNr = nrEval.State is CapabilityState.Supported or CapabilityState.Experimental;
        bool canFg = fgEval.State is CapabilityState.Supported or CapabilityState.SupportedWithLimitations
                     && game.GraphicsApi is GraphicsApi.DirectX12 or GraphicsApi.Unknown
                     && !existing.SelectedFrameGenEngine.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)
                     && !existing.SelectedFrameGenEngine.Contains("AFMF", StringComparison.OrdinalIgnoreCase);
        bool canRr = rrEval.State == CapabilityState.Supported && game.NativeRayReconstructionSupported;

        if (!canSr) existing.EnableSuperResolution = false;
        if (!canNr && existing.NeuralActivationState != NeuralRenderingActivationState.Active)
            existing.EnableNeuralRendering = false;
        if (!canFg || game.NativeFrameGenSupported)
            existing.EnableFrameGeneration = false;
        if (!canRr) existing.EnableRayReconstruction = false;

        if (game.NativeFrameGenSupported)
        {
            existing.DoubleFgGuardTriggered = true;
            existing.DoubleFgWarning = "Your game already supports Frame Generation. Project Luma has disabled duplicate injection to avoid visual artifacts and frame-pacing conflicts.";
        }
        else
        {
            existing.DoubleFgGuardTriggered = false;
            existing.DoubleFgWarning = string.Empty;
        }

        PopulateFriendlyStatuses(game, existing);
        UpdateNeuralActivationDiagnostics(game, existing, gpu, userDlls, manifests, _neuralHubProbeOptions);
        return existing;
    }

    public static void UpdateNeuralActivationDiagnostics(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        DetectedGpuInfo gpu,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        IReadOnlyCollection<BackendManifest> manifests,
        NeuralHubProbeOptions? neuralHubProbeOptions = null)
    {
        bool nrInstalledOnGame = game.InstalledFeatures.Contains(RenderingFeatureType.NeuralRendering)
                                 && game.HealthState == IntegrationHealthState.Healthy;
        if (nrInstalledOnGame)
        {
            profile.NeuralActivationState = NeuralRenderingActivationState.Active;
            profile.NeuralActivationExplanation = "AI Visuals are active and verified on this game.";
            return;
        }

        if (profile.NeuralRenderingState == CapabilityState.Unsupported ||
            profile.NeuralRenderingState == CapabilityState.RequiresGameIntegration ||
            profile.NeuralRenderingState == CapabilityState.RequiresDriver)
        {
            profile.NeuralActivationState = NeuralRenderingActivationState.Unsupported;
            profile.NeuralActivationExplanation = profile.NeuralRenderingReason;
            return;
        }

        if (gpu.Architecture == GpuArchitecture.RDNA2)
        {
            if (gpu.HipRuntimeInstalled && !gpu.HipRuntimeInitialized)
            {
                profile.NeuralActivationState = NeuralRenderingActivationState.RuntimeFailed;
                profile.NeuralActivationExplanation = string.IsNullOrWhiteSpace(gpu.HipInitDiagnostic)
                    ? $"HIP runtime {gpu.HipRuntimeDllName} was found but failed device initialization on {gpu.ModelName} ({gpu.GpuTarget})."
                    : gpu.HipInitDiagnostic;
                return;
            }

            if (!gpu.Hip72SdkAvailable)
            {
                string activeProbe = gpu.HipRuntimeInstalled && gpu.HipRuntimeInitialized
                    ? $"Detected initialized {gpu.HipRuntimeDllName} (HIP API {gpu.HipRuntimeApiVersion}), while AMD HIP SDK 7.2 (amdhip64_7.dll) is absent and AMD official docs exclude {gpu.GpuTarget}."
                    : $"AMD HIP SDK 7.2 (amdhip64_7.dll) is missing and official AMD Windows HIP 7.2 docs exclude {gpu.GpuTarget}.";
                profile.NeuralActivationState = NeuralRenderingActivationState.RuntimeMissing;
                profile.NeuralActivationExplanation = $"{activeProbe} Experimental community RDNA 2 support via DLSS-NR-on-AMD v0.9.0 requires compatible runtime and user-owned nvngx_dlssnr.dll.";
                return;
            }
        }

        var nrDll = userDlls.FirstOrDefault(d => d.DllKind.Equals("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
        var nrManifest = manifests.FirstOrDefault(m => m.Id.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase));
        NeuralHubStatusReport hubReport;
        if (neuralHubProbeOptions == null && string.IsNullOrWhiteSpace(nrManifest?.StagedInstallPath))
            hubReport = NeuralHubInspector.InspectHostQuickCached();
        else
            hubReport = NeuralHubInspector.InspectFullStatus(nrManifest?.StagedInstallPath, nrDll, null, neuralHubProbeOptions, includeGames: false);
        bool nrBackendReady = NeuralHubInspector.HasRuntimeBinaryPayload(nrManifest?.StagedInstallPath) ||
                              (hubReport.IsAppDetected && hubReport.IsRuntimeVerified);

        bool hasNrDll = nrDll != null || hubReport.IsDllImportedInNeuralHub;
        string effectiveVersion = nrDll?.FileVersion ?? hubReport.NeuralHubDllVersion;
        bool supportsCc = (nrDll?.SupportsCcModel == true) ||
                          (hubReport.IsDllImportedInNeuralHub && HasVersionAtLeast(hubReport.NeuralHubDllVersion, new Version(310, 9, 16, 0)));
        bool supportsHt = (nrDll?.SupportsHtModel == true) || HasVersionAtLeast(effectiveVersion, new Version(310, 8, 0, 0));

        bool modelCompatible = hasNrDll && (profile.SelectedNeuralModel == NeuralRenderingModelType.CC ? supportsCc : supportsHt);

        if (!hasNrDll || !nrBackendReady || !modelCompatible)
        {
            profile.NeuralActivationState = NeuralRenderingActivationState.DependencyMissing;
            var missing = new List<string>();
            if (!nrBackendReady) missing.Add("DLSS-NR-on-AMD v0.9.0 / NeuralHub (user-acquired from official GitHub releases)");
            if (!hasNrDll) missing.Add($"user-owned nvngx_dlssnr.dll (>= {(profile.SelectedNeuralModel == NeuralRenderingModelType.CC ? "310.9.16.0" : "310.8.0.0")} for {profile.SelectedNeuralModel} model)");
            else if (!modelCompatible) missing.Add($"nvngx_dlssnr.dll >= 310.9.16.0 for CC model (current v{effectiveVersion} supports HT only)");
            profile.NeuralActivationExplanation = "Missing required dependency: " + string.Join(" and ", missing) + ".";
            return;
        }

        profile.NeuralActivationState = NeuralRenderingActivationState.ReadyToConfigure;
        string sourceNote = nrDll != null && hubReport.IsVaultSynchronizedWithNeuralHub
            ? " (NeuralHub & Vault synchronized)"
            : (nrDll == null ? " (detected in NeuralHub store)" : string.Empty);
        profile.NeuralActivationExplanation = supportsCc
            ? $"Ready (nvngx_dlssnr.dll v{effectiveVersion} supports fast CC & HT models; Reference unavailable on RDNA 2){sourceNote}."
            : $"Ready (nvngx_dlssnr.dll v{effectiveVersion} supports HT model; >= 310.9.16.0 required for CC model){sourceNote}.";
    }

    private static bool HasVersionAtLeast(string? raw, Version min)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string clean = raw.Replace(',', '.').Split(' ', '-', '+')[0].Trim();
        return Version.TryParse(clean, out var v) && v >= min;
    }

    public static void ApplyPreset(
        GameEnhancementProfile profile,
        EnhancementProfilePreset preset,
        DiscoveredGame game,
        DetectedGpuInfo gpu,
        IReadOnlyCollection<UserImportedDllRecord> userDlls)
    {
        profile.SelectedPreset = preset;

        bool canSr = profile.SuperResolutionState is CapabilityState.Supported or CapabilityState.SupportedWithLimitations;
        bool canNr = profile.NeuralRenderingState is CapabilityState.Supported or CapabilityState.Experimental;
        bool canFg = profile.FrameGenerationState is CapabilityState.Supported or CapabilityState.SupportedWithLimitations
                     && game.GraphicsApi is GraphicsApi.DirectX12 or GraphicsApi.Unknown
                     && !profile.SelectedFrameGenEngine.Contains("Unsupported", StringComparison.OrdinalIgnoreCase)
                     && !profile.SelectedFrameGenEngine.Contains("AFMF", StringComparison.OrdinalIgnoreCase);
        bool canRr = profile.RayReconstructionState == CapabilityState.Supported && game.NativeRayReconstructionSupported;

        switch (preset)
        {
            case EnhancementProfilePreset.VisualQuality:
                profile.PresetSummary = "Maximizes image clarity, surface detail and lighting quality using every verified visual enhancement on your PC.";
                profile.EnableSuperResolution = canSr;
                profile.SelectedUpscalingPreset = UpscalingQualityPreset.Quality;
                profile.Sharpness = 0.55;
                profile.EnableNeuralRendering = canNr;
                profile.EnableFrameGeneration = false;
                profile.EnableRayReconstruction = canRr;
                break;

            case EnhancementProfilePreset.Balanced:
                profile.PresetSummary = "Delivers noticeably sharper visuals and smooth performance tailored for everyday gaming on your AMD graphics card.";
                profile.EnableSuperResolution = canSr;
                profile.SelectedUpscalingPreset = UpscalingQualityPreset.Quality;
                profile.Sharpness = 0.50;
                // Keep AI Visual Enhancement opt-in when experimental or missing external dependencies
                profile.EnableNeuralRendering = profile.NeuralRenderingState == CapabilityState.Supported;
                profile.EnableFrameGeneration = canFg && !game.NativeFrameGenSupported;
                profile.EnableRayReconstruction = canRr;
                break;

            case EnhancementProfilePreset.SmootherGameplay:
                profile.PresetSummary = "Prioritizes higher frame rates and fluid motion while maintaining sharp reconstructed image quality.";
                profile.EnableSuperResolution = canSr;
                profile.SelectedUpscalingPreset = UpscalingQualityPreset.Balanced;
                profile.Sharpness = 0.45;
                profile.EnableNeuralRendering = false;
                profile.EnableFrameGeneration = canFg && !game.NativeFrameGenSupported;
                profile.EnableRayReconstruction = false;
                break;

            case EnhancementProfilePreset.Custom:
                profile.PresetSummary = "Customize individual supported visual enhancements and fine-tune quality settings.";
                // Enforce hardware safety limits even in Custom mode
                if (!canSr) profile.EnableSuperResolution = false;
                if (!canNr) profile.EnableNeuralRendering = false;
                if (!canFg) profile.EnableFrameGeneration = false;
                if (!canRr) profile.EnableRayReconstruction = false;
                break;
        }

        if (game.NativeFrameGenSupported)
        {
            profile.EnableFrameGeneration = false; // Prevent double frame generation automatically
            profile.DoubleFgGuardTriggered = true;
            profile.DoubleFgWarning = "Your game already supports Frame Generation. Project Luma has disabled duplicate injection to avoid visual artifacts and frame-pacing conflicts.";
            profile.NativeFrameGenGuide = "Open this game's Graphics settings and enable its built-in Frame Generation option (FSR or DLSS Frame Generation). Keep Project Luma OptiFG and driver AFMF 2 off while native Frame Generation is active.";
        }
        else
        {
            profile.DoubleFgGuardTriggered = false;
            profile.DoubleFgWarning = string.Empty;
            profile.NativeFrameGenGuide = string.Empty;
        }

        PopulateFriendlyStatuses(game, profile);
    }

    public static void PopulateFriendlyStatuses(DiscoveredGame game, GameEnhancementProfile profile)
    {
        profile.AiVisualStatusText = profile.NeuralRenderingState switch
        {
            CapabilityState.Supported => profile.EnableNeuralRendering ? "Included in your profile" : "Ready",
            CapabilityState.Experimental => "Experimental",
            CapabilityState.RequiresDependency or CapabilityState.RequiresDriver or CapabilityState.RequiresGameIntegration => "Setup required",
            _ => "Not supported"
        };

        profile.SharperImageStatusText = profile.SuperResolutionState switch
        {
            CapabilityState.Supported when profile.EnableSuperResolution => "Included in your profile",
            CapabilityState.Supported => "Ready",
            CapabilityState.SupportedWithLimitations => "Experimental",
            CapabilityState.RequiresDependency or CapabilityState.RequiresDriver or CapabilityState.RequiresGameIntegration => "Setup required",
            _ => "Not supported"
        };

        if (game.NativeFrameGenSupported)
        {
            profile.EnableFrameGeneration = false;
            profile.DoubleFgGuardTriggered = true;
            if (string.IsNullOrWhiteSpace(profile.DoubleFgWarning))
                profile.DoubleFgWarning = "Your game already supports Frame Generation. Project Luma has disabled duplicate injection to avoid visual artifacts and frame-pacing conflicts.";
            if (string.IsNullOrWhiteSpace(profile.NativeFrameGenGuide))
                profile.NativeFrameGenGuide = "Open this game's Graphics settings and enable its built-in Frame Generation option (FSR or DLSS Frame Generation). Keep Project Luma OptiFG and driver AFMF 2 off while native Frame Generation is active.";
            profile.SmootherGameplayStatusText = "Already available in this game";
        }
        else if (profile.FrameGenerationState == CapabilityState.Unsupported || !game.Is64Bit ||
                 game.AntiCheatStatus is AntiCheatStatus.DetectedBlocked or AntiCheatStatus.WindowsAppsProtected)
        {
            profile.EnableFrameGeneration = false;
            profile.SmootherGameplayStatusText = "Not supported";
        }
        else if (profile.SelectedFrameGenEngine.Contains("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            profile.EnableFrameGeneration = false;
            profile.SmootherGameplayStatusText = "Unavailable on RX 6000";
        }
        else if (game.GraphicsApi == GraphicsApi.DirectX11 || game.GraphicsApi == GraphicsApi.Vulkan ||
                 profile.SelectedFrameGenEngine.Contains("AFMF", StringComparison.OrdinalIgnoreCase))
        {
            profile.EnableFrameGeneration = false;
            profile.SmootherGameplayStatusText = "Use AMD Adrenalin AFMF 2";
        }
        else
        {
            profile.SmootherGameplayStatusText = profile.FrameGenerationState switch
            {
                CapabilityState.Supported when profile.EnableFrameGeneration => "Included in your profile",
                CapabilityState.Supported => "Ready",
                CapabilityState.SupportedWithLimitations when profile.EnableFrameGeneration => "Included in your profile",
                CapabilityState.SupportedWithLimitations => "Ready",
                CapabilityState.RequiresDependency or CapabilityState.RequiresDriver or CapabilityState.RequiresGameIntegration => "Setup required",
                _ => "Not supported"
            };
        }

        profile.RayDetailsStatusText = profile.RayReconstructionState switch
        {
            CapabilityState.Supported => profile.EnableRayReconstruction ? "Included in your profile" : "Ready",
            CapabilityState.Experimental => "Experimental",
            CapabilityState.RequiresDependency or CapabilityState.RequiresDriver or CapabilityState.RequiresGameIntegration => "Setup required",
            _ => "Not supported"
        };

        if (game.AntiCheatStatus is AntiCheatStatus.DetectedBlocked or AntiCheatStatus.WindowsAppsProtected || !game.Is64Bit)
        {
            game.FriendlyStatus = FriendlyGameStatus.NotSupported;
            game.FriendlyStatusText = "Unsupported";
            game.RecommendationSummary = game.AntiCheatStatus == AntiCheatStatus.None
                ? "Older 32-bit titles are not supported by modern enhancement pipelines."
                : "Protected by online anti-cheat or Windows Store permissions.";
        }
        else if (!string.IsNullOrWhiteSpace(game.InstalledBackendId))
        {
            game.FriendlyStatus = FriendlyGameStatus.Enhanced;
            game.FriendlyStatusText = "Enhanced";
            game.RecommendationSummary = "Project Luma enhancements are active and verified on this game.";
        }
        else if (profile.SuperResolutionState == CapabilityState.Supported)
        {
            game.FriendlyStatus = FriendlyGameStatus.ReadyToEnhance;
            game.FriendlyStatusText = "Ready to Enhance";
            game.RecommendationSummary = "Ready for 1-click Sharper Image and Smoother Gameplay enhancements on your AMD GPU.";
        }
        else if (profile.SuperResolutionState == CapabilityState.SupportedWithLimitations ||
                 profile.FrameGenerationState == CapabilityState.SupportedWithLimitations)
        {
            game.FriendlyStatus = FriendlyGameStatus.LimitedSupport;
            game.FriendlyStatusText = "Setup Required";
            game.RecommendationSummary = "Standard enhancements are available with API or hardware limitations.";
        }
        else
        {
            game.FriendlyStatus = FriendlyGameStatus.SetupRequired;
            game.FriendlyStatusText = "Setup Required";
            game.RecommendationSummary = "One or more required dependencies need attention before enhancing.";
        }
    }

    public static FeatureCapabilityEvaluation EvaluateNeuralRendering(
        DetectedGpuInfo gpu,
        DiscoveredGame? game,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        UpdateChannel channel,
        NeuralHubProbeOptions? neuralHubProbeOptions = null)
    {
        var eval = new FeatureCapabilityEvaluation
        {
            Feature = RenderingFeatureType.NeuralRendering,
            RecommendedBackendId = "dlss-nr-on-amd",
            RecommendedBackendName = "DLSS-NR-on-AMD (NeuralHub)"
        };

        if (game != null)
        {
            if (game.AntiCheatStatus == AntiCheatStatus.DetectedBlocked || game.AntiCheatStatus == AntiCheatStatus.WindowsAppsProtected)
            {
                eval.State = CapabilityState.Unsupported;
                eval.StatusBadgeText = "Blocked (Anti-Cheat)";
                eval.Summary = "Disabled due to anti-cheat / protected folder.";
                eval.DetailedReason = $"Game cannot safely load neural rendering proxy DLLs: {game.AntiCheatDetails}.";
                eval.CanEnable = false;
                return eval;
            }
            if (!game.Is64Bit)
            {
                eval.State = CapabilityState.Unsupported;
                eval.StatusBadgeText = "Unsupported (32-bit)";
                eval.Summary = "Requires 64-bit DirectX 12 executable.";
                eval.DetailedReason = "DLSS-NR-on-AMD only supports 64-bit DirectX 12 applications.";
                eval.CanEnable = false;
                return eval;
            }
            if (game.GraphicsApi != GraphicsApi.Unknown && game.GraphicsApi != GraphicsApi.DirectX12)
            {
                eval.State = CapabilityState.RequiresGameIntegration;
                eval.StatusBadgeText = "Requires DX12";
                eval.Summary = $"Game uses {game.GraphicsApi}; Neural Rendering requires DirectX 12.";
                eval.DetailedReason = $"Switch {game.Title} to DirectX 12 if available. Vulkan and DX11 are not supported by DLSS-NR-on-AMD.";
                eval.CanEnable = false;
                return eval;
            }
        }

        if (gpu.Architecture is GpuArchitecture.Unknown or GpuArchitecture.GCN or GpuArchitecture.RDNA1)
        {
            eval.State = CapabilityState.Unsupported;
            eval.StatusBadgeText = "Unsupported";
            eval.Summary = "Requires RDNA 2 (RX 6000) or newer.";
            eval.DetailedReason = $"Detected {gpu.ModelName} ({gpu.Architecture}). DLSS-NR-on-AMD requires AMD RDNA 2, RDNA 3, or RDNA 4 with HIP compute support.";
            eval.CanEnable = false;
            return eval;
        }

        if (gpu.DriverVersion != "Unknown" && !gpu.MeetsAdrenalin2611Minimum)
        {
            eval.State = CapabilityState.RequiresDriver;
            eval.StatusBadgeText = "Requires Driver 26.1.1+";
            eval.Summary = $"Installed WDDM {gpu.DriverVersion} may predate Adrenalin 26.1.1.";
            eval.DetailedReason = "DLSS-NR-on-AMD requires AMD Software: Adrenalin Edition 26.1.1 or newer.";
            eval.MissingPrerequisites.Add("AMD Adrenalin 26.1.1 or later");
            eval.CanEnable = false;
            return eval;
        }

        var nrDllRecord = userDlls.FirstOrDefault(d => d.DllKind.Equals("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
        bool hasNrDll = nrDllRecord != null || NeuralHubInspector.InspectHostQuickCached(neuralHubProbeOptions).IsDllImportedInNeuralHub;
        if (!hasNrDll)
            eval.MissingPrerequisites.Add("User-owned nvngx_dlssnr.dll (310.8.0.0+ for HT, 310.9.16+ for CC)");

        if (gpu.Architecture == GpuArchitecture.RDNA2)
        {
            if (!gpu.Hip72SdkAvailable)
                eval.MissingPrerequisites.Add("Experimental AMD HIP runtime (amdhip64_7.dll — note AMD HIP 7.2 docs list gfx1032 as unsupported)");

            eval.Warnings.Add("RDNA 2 (RX 6000 / gfx1032) runs Neural Rendering on an experimental community HIP path. Fast and Superfast modes are supported in DLSS-NR-on-AMD v0.9.0; Reference mode is unavailable, and CC model requires nvngx_dlssnr.dll >= 310.9.16.0.");

            if (eval.MissingPrerequisites.Count > 0)
            {
                eval.State = CapabilityState.RequiresDependency;
                eval.StatusBadgeText = "Setup required";
                eval.Summary = $"Experimental on RDNA 2 ({(string.IsNullOrWhiteSpace(gpu.GpuTarget) ? "gfx103x" : gpu.GpuTarget)}); missing {eval.MissingPrerequisites.Count} prerequisite(s).";
                eval.DetailedReason = "Hardware capability is Experimental on RDNA 2 (not officially listed in AMD HIP 7.2 Windows requirements), and requires: " +
                                      string.Join("; ", eval.MissingPrerequisites) + ".";
                eval.CanEnable = false;
                return eval;
            }

            eval.State = CapabilityState.Experimental;
            eval.StatusBadgeText = "Experimental";
            eval.Summary = "Experimental RDNA 2 Neural Rendering ready (Fast / Superfast).";
            eval.DetailedReason = "Experimental RDNA 2 community HIP path detected with user nvngx_dlssnr.dll. Reference mode remains unavailable on RX 6000.";
            eval.CanEnable = true;
            return eval;
        }

        // RDNA 3 / RDNA 4
        if (eval.MissingPrerequisites.Count > 0)
        {
            eval.State = CapabilityState.RequiresDependency;
            eval.StatusBadgeText = "Requires User DLL";
            eval.Summary = "Import your own nvngx_dlssnr.dll to activate.";
            eval.DetailedReason = $"{gpu.Architecture} supports native D3D12 Neural Rendering; import your own legitimate nvngx_dlssnr.dll (310.8.0.0+) in Enhancement Centre.";
            eval.CanEnable = false;
            return eval;
        }

        eval.State = CapabilityState.Supported;
        eval.StatusBadgeText = "Supported";
        eval.Summary = $"Native D3D12 Neural Rendering ready on {gpu.Architecture}.";
        eval.DetailedReason = "All Neural Rendering prerequisites verified (Native D3D12 compute path).";
        eval.CanEnable = true;
        return eval;
    }

    public static FeatureCapabilityEvaluation EvaluateSuperResolution(
        DetectedGpuInfo gpu,
        DiscoveredGame? game,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        UpdateChannel channel)
    {
        var eval = new FeatureCapabilityEvaluation
        {
            Feature = RenderingFeatureType.SuperResolution,
            RecommendedBackendId = "optiscaler",
            RecommendedBackendName = "OptiScaler v0.9.5 (FFX 2.4 / XeSS 3.0.2)"
        };

        if (game != null && (game.AntiCheatStatus == AntiCheatStatus.DetectedBlocked || game.AntiCheatStatus == AntiCheatStatus.WindowsAppsProtected))
        {
            eval.State = CapabilityState.Unsupported;
            eval.StatusBadgeText = "Blocked (Anti-Cheat)";
            eval.Summary = "Disabled due to anti-cheat / protected folder.";
            eval.DetailedReason = $"Cannot inject upscaling proxy into protected title: {game.AntiCheatDetails}.";
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && !game.Is64Bit)
        {
            eval.State = CapabilityState.Unsupported;
            eval.StatusBadgeText = "Unsupported (32-bit)";
            eval.Summary = "32-bit executables are unsupported by modern upscalers.";
            eval.DetailedReason = "OptiScaler and FidelityFX SDK require 64-bit Windows executables.";
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && !game.NativeDlssSupported && !game.NativeFsrSupported && !game.NativeXeSsSupported)
        {
            eval.Warnings.Add("No native temporal upscaler DLL (DLSS/FSR/XeSS) was detected directly beside the executable; the game may embed it inside plugins or lack temporal upscaling inputs.");
        }

        switch (gpu.Architecture)
        {
            case GpuArchitecture.RDNA4:
                eval.State = CapabilityState.Supported;
                eval.StatusBadgeText = "Supported (FSR4 FP8)";
                eval.Summary = "FSR 4.1.2 FP8, FSR 3.1.2, XeSS 2.0.2, and DLSS-SR 4.5 supported.";
                eval.DetailedReason = "RDNA 4 supports native FP8 FSR 4.1.2 via OptiScaler v0.9.5 and DLSS-SR via DLSS-NR-on-AMD.";
                eval.CanEnable = true;
                break;

            case GpuArchitecture.RDNA3:
                eval.State = CapabilityState.Supported;
                eval.StatusBadgeText = "Supported (FSR4 INT8)";
                eval.Summary = "FSR 4.1.2 INT8, FSR 3.1.2, XeSS 2.0.2, and DLSS-SR 4.5 supported.";
                eval.DetailedReason = "RDNA 3 supports INT8 FSR 4.1.2 via OptiScaler v0.9.5 and native D3D12 DLSS-SR via DLSS-NR-on-AMD.";
                eval.CanEnable = true;
                break;

            case GpuArchitecture.RDNA2:
                eval.State = CapabilityState.Supported;
                eval.StatusBadgeText = "Supported (FSR4 INT8)";
                eval.Summary = "FSR 4.1.2 INT8 (FSR4-i8-2), FSR 3.1.2, and XeSS 2.0.2 via OptiScaler.";
                eval.DetailedReason = "RDNA 2 (RX 6000) receives official FSR 4.1.2 INT8 support (FSR4-i8-2) in FidelityFX SDK 2.4 / OptiScaler v0.9.5, plus FSR 3.1.2 and XeSS 2.0.2. Note: DLSS-SR via DLSS-NR-on-AMD v0.9.0 uses an experimental RDNA 2 HIP path.";
                eval.CanEnable = true;
                break;

            case GpuArchitecture.RDNA1:
            case GpuArchitecture.GCN:
                eval.State = CapabilityState.SupportedWithLimitations;
                eval.StatusBadgeText = "Supported (FSR 3.1 / XeSS)";
                eval.Summary = "FSR 3.1.2, FSR 2.2.1, and XeSS 2.0.2 supported; no official FSR 4 INT8.";
                eval.DetailedReason = $"{gpu.Architecture} lacks official INT8 FSR 4 validation; OptiScaler falls back cleanly to FSR 3.1.2 or XeSS 2.0.2.";
                eval.CanEnable = true;
                break;

            default:
                eval.State = CapabilityState.Unsupported;
                eval.StatusBadgeText = "Unsupported";
                eval.Summary = "Unsupported GPU architecture.";
                eval.DetailedReason = "Project Luma targets AMD Radeon RDNA architectures.";
                eval.CanEnable = false;
                break;
        }

        return eval;
    }

    public static FeatureCapabilityEvaluation EvaluateFrameGeneration(
        DetectedGpuInfo gpu,
        DiscoveredGame? game,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        UpdateChannel channel)
    {
        var eval = new FeatureCapabilityEvaluation
        {
            Feature = RenderingFeatureType.FrameGeneration,
            RecommendedBackendId = "optiscaler",
            RecommendedBackendName = "OptiScaler (FSR-FG / OptiFG / Nukem) & Guided AFMF 2"
        };

        if (game != null && (game.AntiCheatStatus == AntiCheatStatus.DetectedBlocked || game.AntiCheatStatus == AntiCheatStatus.WindowsAppsProtected))
        {
            eval.State = CapabilityState.Unsupported;
            eval.StatusBadgeText = "Blocked (Anti-Cheat)";
            eval.Summary = "Disabled due to anti-cheat / protected folder.";
            eval.DetailedReason = $"Frame Generation DLL injection blocked: {game.AntiCheatDetails}.";
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && !game.Is64Bit)
        {
            eval.State = CapabilityState.Unsupported;
            eval.StatusBadgeText = "Unsupported (32-bit)";
            eval.Summary = "32-bit executables are unsupported by modern Frame Generation backends.";
            eval.DetailedReason = $"{game.Title} is a 32-bit executable; OptiFG and FSR-FG require 64-bit games.";
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && game.GraphicsApi == GraphicsApi.DirectX11)
        {
            eval.State = CapabilityState.SupportedWithLimitations;
            eval.StatusBadgeText = "AFMF 2 Guided Only (DX11)";
            eval.Summary = "OptiFG requires DX12; use Guided Adrenalin AFMF 2 for DX11 games.";
            eval.DetailedReason = $"{game.Title} uses DirectX 11. OptiFG and FSR-FG require DirectX 12; driver-level AFMF 2 can be enabled inside AMD Adrenalin (Exclusive Fullscreen + VSync OFF on RDNA 2).";
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && game.GraphicsApi == GraphicsApi.Vulkan)
        {
            bool canAfmf = gpu.Architecture >= GpuArchitecture.RDNA2;
            eval.State = canAfmf ? CapabilityState.SupportedWithLimitations : CapabilityState.Unsupported;
            eval.StatusBadgeText = canAfmf ? "AFMF 2 Guided Only (Vulkan)" : "Unsupported (Vulkan)";
            eval.Summary = canAfmf
                ? "OptiFG requires DX12; use Guided Adrenalin AFMF 2 for Vulkan games."
                : "Vulkan Frame Generation is unsupported on this GPU.";
            eval.DetailedReason = $"{game.Title} uses Vulkan. In-game OptiFG DLL injection targets DirectX 12; {(canAfmf ? "use AMD Adrenalin AFMF 2 instead." : "upgrade to RDNA 2+ for AFMF 2.")}";
            eval.CanEnable = false;
            return eval;
        }

        if (gpu.Architecture == GpuArchitecture.RDNA2)
        {
            eval.State = CapabilityState.Supported;
            eval.StatusBadgeText = "Supported (FSR-FG / OptiFG)";
            eval.Summary = "FSR-FG 3.1/4.0.2, OptiFG DX12, and Nukem dlssg-to-fsr3 supported. DLSS-FG is Unsupported.";
            eval.DetailedReason = "On RDNA 2 (RX 6600), OptiScaler provides FSR Frame Generation, OptiFG (with HUDfix), and Nukem dlssg-to-fsr3. DLSS-FG via DLSS-NR-on-AMD is explicitly unsupported on RX 6000. Driver-level AFMF 2 is available via guided Adrenalin setup (avoid combining with in-game FG).";
            eval.Warnings.Add("DLSS Frame Generation (DLSS-NR-on-AMD) is unsupported on RX 6000 (RDNA 2).");
            eval.CanEnable = true;
            return eval;
        }

        if (gpu.Architecture is GpuArchitecture.RDNA3 or GpuArchitecture.RDNA4)
        {
            eval.State = CapabilityState.Supported;
            eval.StatusBadgeText = "Supported (All FG Modes)";
            eval.Summary = "FSR-FG 4.0.2, OptiFG, Nukem, AFMF 2, and DLSS-FG supported.";
            eval.DetailedReason = $"{gpu.Architecture} supports both OptiScaler FSR-FG / OptiFG and DLSS-FG (via DLSS-NR-on-AMD with user nvngx_dlssg.dll).";
            eval.CanEnable = true;
            return eval;
        }

        if (gpu.Architecture == GpuArchitecture.RDNA1)
        {
            eval.State = CapabilityState.SupportedWithLimitations;
            eval.StatusBadgeText = "Supported (OptiFG DX12)";
            eval.Summary = "OptiFG / FSR3-FG DX12 supported; no driver AFMF 2.";
            eval.DetailedReason = "RDNA 1 supports DX12 FSR3-FG / OptiFG via OptiScaler with Anti-Lag 2.";
            eval.CanEnable = true;
            return eval;
        }

        eval.State = CapabilityState.Unsupported;
        eval.StatusBadgeText = "Unsupported";
        eval.Summary = "No compatible Frame Generation path for this GPU.";
        eval.DetailedReason = "Frame Generation requires RDNA 1+ for OptiFG or RDNA 2+ for AFMF 2.";
        eval.CanEnable = false;
        return eval;
    }

    public static FeatureCapabilityEvaluation EvaluateRayReconstruction(
        DetectedGpuInfo gpu,
        DiscoveredGame? game,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        UpdateChannel channel)
    {
        var eval = new FeatureCapabilityEvaluation
        {
            Feature = RenderingFeatureType.RayReconstruction,
            RecommendedBackendId = "dlss-nr-on-amd",
            RecommendedBackendName = "DLSS-RR on AMD (Model F)"
        };

        if (gpu.Architecture is GpuArchitecture.Unknown or GpuArchitecture.GCN or GpuArchitecture.RDNA1 or GpuArchitecture.RDNA2)
        {
            eval.State = CapabilityState.Unsupported;
            eval.StatusBadgeText = "Unsupported";
            eval.Summary = "Requires RDNA 3 (RX 7000) or RDNA 4 (RX 9000).";
            eval.DetailedReason = gpu.Architecture == GpuArchitecture.RDNA2
                ? "DLSS Ray Reconstruction (Model F) targets RDNA 3 and RDNA 4 D3D12 pipelines. On RX 6000 (RDNA 2), official AMD HIP 7.2 support is excluded for gfx1032 and Ray Reconstruction is not recommended; use Super Resolution (FSR 4.1.2 INT8 / FSR 3.1) instead."
                : $"Ray Reconstruction is not supported on {gpu.Architecture}.";
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && (game.AntiCheatStatus == AntiCheatStatus.DetectedBlocked || game.AntiCheatStatus == AntiCheatStatus.WindowsAppsProtected))
        {
            eval.State = CapabilityState.Unsupported;
            eval.StatusBadgeText = "Blocked (Anti-Cheat)";
            eval.Summary = "Disabled due to anti-cheat / protected folder.";
            eval.DetailedReason = game.AntiCheatDetails;
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && game.GraphicsApi != GraphicsApi.Unknown && game.GraphicsApi != GraphicsApi.DirectX12)
        {
            eval.State = CapabilityState.RequiresGameIntegration;
            eval.StatusBadgeText = "Requires DX12 RT";
            eval.Summary = "Ray Reconstruction requires a DirectX 12 ray-traced game.";
            eval.DetailedReason = $"{game.Title} is {game.GraphicsApi} and lacks DX12 DLSS-D ray tracing inputs.";
            eval.CanEnable = false;
            return eval;
        }

        bool hasRrDll = userDlls.Any(d => d.DllKind.Equals("nvngx_dlssd", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
        if (!hasRrDll)
        {
            eval.State = CapabilityState.RequiresDependency;
            eval.StatusBadgeText = "Requires nvngx_dlssd.dll";
            eval.Summary = "Import your own nvngx_dlssd.dll to enable Ray Reconstruction.";
            eval.DetailedReason = $"{gpu.Architecture} supports DLSS Ray Reconstruction Model F, but requires a user-imported nvngx_dlssd.dll.";
            eval.MissingPrerequisites.Add("User-owned nvngx_dlssd.dll");
            eval.CanEnable = false;
            return eval;
        }

        if (game != null && !game.NativeRayReconstructionSupported && !game.NativeDlssSupported)
        {
            eval.State = CapabilityState.RequiresGameIntegration;
            eval.StatusBadgeText = "Requires Game RT Inputs";
            eval.Summary = "Game does not expose native DLSS-RR inputs.";
            eval.DetailedReason = "Ray Reconstruction requires temporal ray-tracing buffers (diffuse/specular hit distance and albedo) supplied via DLSS-D.";
            eval.CanEnable = false;
            return eval;
        }

        eval.State = CapabilityState.Supported;
        eval.StatusBadgeText = "Supported";
        eval.Summary = $"DLSS Ray Reconstruction (Model F) ready on {gpu.Architecture}.";
        eval.DetailedReason = "Hardware and user nvngx_dlssd.dll verified.";
        eval.CanEnable = true;
        return eval;
    }

    private static string FormatSummarySlug(CapabilityState state) => state switch
    {
        CapabilityState.Supported => "supported",
        CapabilityState.SupportedWithLimitations => "supported_with_limitations",
        CapabilityState.Experimental => "experimental",
        CapabilityState.RequiresDependency => "requires_dependency",
        CapabilityState.RequiresDriver => "requires_driver",
        CapabilityState.RequiresGameIntegration => "requires_game_integration",
        _ => "unsupported"
    };
}
