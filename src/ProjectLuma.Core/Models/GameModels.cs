using ProjectLuma.Core.Enums;

namespace ProjectLuma.Core.Models;

/// <summary>Represents a discovered PC game and its verified binary characteristics.</summary>
public sealed class DiscoveredGame
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableDirectory { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = string.Empty;
    public GameLauncherType Launcher { get; set; }
    public string StoreAppId { get; set; } = string.Empty;
    public GraphicsApi GraphicsApi { get; set; } = GraphicsApi.Unknown;
    public string EngineName { get; set; } = "Unknown";
    public bool Is64Bit { get; set; }
    public bool ImportsVersionDll { get; set; }
    public bool ImportsDxgiDll { get; set; }
    public bool NativeDlssSupported { get; set; }
    public bool NativeFsrSupported { get; set; }
    public string NativeFsrVersion { get; set; } = string.Empty;
    public bool NativeXeSsSupported { get; set; }
    public bool NativeFrameGenSupported { get; set; }
    public bool NativeRayReconstructionSupported { get; set; }
    public AntiCheatStatus AntiCheatStatus { get; set; } = AntiCheatStatus.None;
    public string AntiCheatDetails { get; set; } = string.Empty;
    public GameCompatibilityTier CompatibilityTier { get; set; } = GameCompatibilityTier.CompatibleUntested;
    public IntegrationHealthState HealthState { get; set; } = IntegrationHealthState.NotInstalled;
    public string InstalledBackendId { get; set; } = string.Empty;
    public string InstalledBackendVersion { get; set; } = string.Empty;
    public List<RenderingFeatureType> InstalledFeatures { get; set; } = new();
    public DateTime LastScannedUtc { get; set; } = DateTime.UtcNow;

    // Redesigned Gaming Library Artwork & Friendly Status Metadata
    public string CoverImagePath { get; set; } = string.Empty;
    public string HeroImagePath { get; set; } = string.Empty;
    public string LogoImagePath { get; set; } = string.Empty;
    public string IconImagePath { get; set; } = string.Empty;
    public ArtworkSourceKind ArtworkSource { get; set; } = ArtworkSourceKind.None;
    public FriendlyGameStatus FriendlyStatus { get; set; } = FriendlyGameStatus.ReadyToEnhance;
    public string FriendlyStatusText { get; set; } = "Ready to Enhance";
    public string RecommendationSummary { get; set; } = string.Empty;
    public DateTime? LastEnhancedUtc { get; set; }

    // Manual addition & persistent availability metadata
    public bool IsManuallyAdded { get; set; }
    public string CustomInstallLocation { get; set; } = string.Empty;
    public bool IsAvailable { get; set; } = true;
    public string AvailabilityReason { get; set; } = string.Empty;
    public DateTime? AddedAtUtc { get; set; }
    public bool HasCustomArtwork { get; set; }
}

/// <summary>Dynamic per-game enhancement profile evaluated against the current GPU and backend versions.</summary>
public sealed class GameEnhancementProfile
{
    public string GameId { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public CapabilityState NeuralRenderingState { get; set; } = CapabilityState.Unsupported;
    public CapabilityState SuperResolutionState { get; set; } = CapabilityState.Unsupported;
    public CapabilityState FrameGenerationState { get; set; } = CapabilityState.Unsupported;
    public CapabilityState RayReconstructionState { get; set; } = CapabilityState.Unsupported;

    public string NeuralRenderingReason { get; set; } = string.Empty;
    public string SuperResolutionReason { get; set; } = string.Empty;
    public string FrameGenerationReason { get; set; } = string.Empty;
    public string RayReconstructionReason { get; set; } = string.Empty;

    public bool EnableNeuralRendering { get; set; }
    public NeuralQualityMode SelectedNeuralQuality { get; set; } = NeuralQualityMode.Fast;
    public NeuralRenderingModelType SelectedNeuralModel { get; set; } = NeuralRenderingModelType.CC;
    public bool EnableNeuralMultipass { get; set; }

    public bool EnableSuperResolution { get; set; } = true;
    public string SelectedUpscalerBackend { get; set; } = "optiscaler";
    public string SelectedUpscalerEngine { get; set; } = "FSR 4.1.2 INT8 (FSR4-i8-2)";
    public UpscalingQualityPreset SelectedUpscalingPreset { get; set; } = UpscalingQualityPreset.Quality;
    public double Sharpness { get; set; } = 0.5;
    public bool ShowFsr4Watermark { get; set; }

    public bool EnableFrameGeneration { get; set; }
    public string SelectedFrameGenEngine { get; set; } = "OptiFG DX12";
    public bool EnableHudFix { get; set; } = true;
    public bool EnableAntiLag2 { get; set; } = true;
    public bool DoubleFgGuardTriggered { get; set; }
    public string DoubleFgWarning { get; set; } = string.Empty;
    public string NativeFrameGenGuide { get; set; } = string.Empty;

    public bool EnableRayReconstruction { get; set; }
    public string ProxyDllName { get; set; } = "dxgi.dll";
    public bool OfflineAntiCheatConfirmed { get; set; }

    public List<string> TestedCombinations { get; set; } = new();
    public List<string> UntestedCombinations { get; set; } = new();
    public List<string> KnownProblems { get; set; } = new();
    public List<string> RequiredDlls { get; set; } = new();
    public Dictionary<string, string> RequiredSettings { get; set; } = new();
    public string MinimumDriverVersion { get; set; } = "26.1.1";
    public string TargetBackendVersion { get; set; } = "v0.9.5";
    public string GraphicsApiRequirements { get; set; } = "DirectX 12";
    public string AntiCheatRestrictions { get; set; } = "None detected";
    public string LastVerificationDate { get; set; } = string.Empty;

    // Beginner-Friendly Profile & Progressive Disclosure State
    public EnhancementProfilePreset SelectedPreset { get; set; } = EnhancementProfilePreset.Balanced;
    public string PresetSummary { get; set; } = "Delivers noticeably sharper visuals and smoother performance tailored for your PC.";
    public string AiVisualStatusText { get; set; } = "Experimental";
    public string SharperImageStatusText { get; set; } = "Included in recommended profile";
    public string SmootherGameplayStatusText { get; set; } = "Ready";
    public string RayDetailsStatusText { get; set; } = "Not available on your GPU";
    public NeuralRenderingActivationState NeuralActivationState { get; set; } = NeuralRenderingActivationState.DependencyMissing;
    public string NeuralActivationExplanation { get; set; } = string.Empty;
}

/// <summary>Seed compatibility record from data/game_profiles/game_compatibility.json.</summary>
public sealed class CompatibilitySeedProfile
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public string SteamAppId { get; set; } = string.Empty;
    public string RequiredApi { get; set; } = "DirectX12";
    public string MinimumDriverVersion { get; set; } = "26.1.1";
    public bool TestedOnRx6600 { get; set; }
    public string NeuralRenderingNotes { get; set; } = string.Empty;
    public string SuperResolutionNotes { get; set; } = string.Empty;
    public string FrameGenerationNotes { get; set; } = string.Empty;
    public string RayReconstructionNotes { get; set; } = string.Empty;
    public string AntiCheatStatus { get; set; } = "None";
    public List<string> RequiredDlls { get; set; } = new();
    public List<string> KnownProblems { get; set; } = new();
    public Dictionary<string, string> RecommendedSettings { get; set; } = new();
    public string LastVerificationDate { get; set; } = string.Empty;
}

/// <summary>Progress snapshot during background library discovery.</summary>
public sealed class GameLibraryScanProgress
{
    public string CurrentSource { get; set; } = string.Empty;
    public int DiscoveredCount { get; set; }
    public int PercentComplete { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}
