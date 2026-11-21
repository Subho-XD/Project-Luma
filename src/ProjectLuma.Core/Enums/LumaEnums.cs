namespace ProjectLuma.Core.Enums;

/// <summary>AMD and general GPU microarchitecture tiers.</summary>
public enum GpuArchitecture
{
    Unknown = 0,
    GCN = 1,
    RDNA1 = 2,
    RDNA2 = 3,
    RDNA3 = 4,
    RDNA4 = 5,
    FutureRDNA = 6
}

/// <summary>The four independent rendering enhancement pillars.</summary>
public enum RenderingFeatureType
{
    NeuralRendering = 0,
    SuperResolution = 1,
    FrameGeneration = 2,
    RayReconstruction = 3
}

/// <summary>Mandatory seven-state capability model.</summary>
public enum CapabilityState
{
    Supported = 0,
    SupportedWithLimitations = 1,
    Experimental = 2,
    RequiresDependency = 3,
    RequiresDriver = 4,
    RequiresGameIntegration = 5,
    Unsupported = 6
}

/// <summary>Standard upscaling quality presets.</summary>
public enum UpscalingQualityPreset
{
    NativeAA = 0,
    UltraQuality = 1,
    Quality = 2,
    Balanced = 3,
    Performance = 4,
    UltraPerformance = 5
}

/// <summary>Neural Rendering quality tiers for DLSS-NR-on-AMD.</summary>
public enum NeuralQualityMode
{
    Superfast = 0,
    Fast = 1,
    Reference = 2
}

/// <summary>Supported PC game launchers and manual folder registration.</summary>
public enum GameLauncherType
{
    Steam = 0,
    EpicGames = 1,
    GogGalaxy = 2,
    UbisoftConnect = 3,
    EaApp = 4,
    BattleNet = 5,
    Xbox = 6,
    Manual = 7
}

/// <summary>Detected rendering API for a game executable.</summary>
public enum GraphicsApi
{
    Unknown = 0,
    DirectX11 = 1,
    DirectX12 = 2,
    Vulkan = 3
}

/// <summary>Anti-cheat detection classification.</summary>
public enum AntiCheatStatus
{
    None = 0,
    DetectedBlocked = 1,
    DetectedOfflineAllowed = 2,
    WindowsAppsProtected = 3
}

/// <summary>Release update channel preference.</summary>
public enum UpdateChannel
{
    Stable = 0,
    Experimental = 1
}

/// <summary>Trust and cryptographic verification level for downloaded packages and imported DLLs.</summary>
public enum VerificationLevel
{
    VerifiedSignatureAndHttps = 0,
    VerifiedHashAndHttps = 1,
    OfficialHttpsReducedVerification = 2,
    OfficialSystemDriver = 3,
    LocalUserFileVerified = 4,
    Unverified = 5
}

/// <summary>Health state of an installed per-game enhancement integration.</summary>
public enum IntegrationHealthState
{
    NotInstalled = 0,
    Healthy = 1,
    DriftDetected = 2,
    MissingFiles = 3,
    BackupCorrupted = 4
}

/// <summary>Compatibility classification tier for discovered games.</summary>
public enum GameCompatibilityTier
{
    Tested = 0,
    CompatibleUntested = 1,
    Unsupported = 2
}

/// <summary>Structured log severity level.</summary>
public enum LumaLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4
}

/// <summary>Plain-English library status displayed on game cards and hero banners.</summary>
public enum FriendlyGameStatus
{
    ReadyToEnhance = 0,
    Enhanced = 1,
    SetupRequired = 2,
    LimitedSupport = 3,
    NotSupported = 4,
    Unavailable = 5
}

/// <summary>Explicit nine-state lifecycle for Project Luma self-updates.</summary>
public enum AppUpdateState
{
    Idle = 0,
    CheckingForUpdates = 1,
    AlreadyUpToDate = 2,
    UpdateAvailable = 3,
    Downloading = 4,
    VerifyingPackage = 5,
    PreparingInstallation = 6,
    Installing = 7,
    UpdateCompleted = 8,
    UpdateFailed = 9
}

/// <summary>One-click gamer enhancement profile presets.</summary>
public enum EnhancementProfilePreset
{
    Balanced = 0,
    VisualQuality = 1,
    SmootherGameplay = 2,
    Custom = 3
}

/// <summary>Source of resolved game artwork.</summary>
public enum ArtworkSourceKind
{
    None = 0,
    SteamLocalCache = 1,
    LauncherMetadata = 2,
    SteamCdn = 3,
    SteamGridDbV2 = 4,
    GeneratedFallback = 5,
    CustomUser = 6
}

/// <summary>Execution state of a step inside the friendly setup workflow.</summary>
public enum SetupStepState
{
    Pending = 0,
    InProgress = 1,
    Complete = 2,
    AttentionRequired = 3,
    Failed = 4
}

/// <summary>Component installation lifecycle state reported by Intelligent Component Manager.</summary>
public enum ComponentInstallState
{
    Installed = 0,
    ReadyToInstall = 1,
    UpdateAvailable = 2,
    RequiresSetup = 3,
    DriverManaged = 4,
    Experimental = 5,
    HardwareIncompatible = 6,
    ExternalAcquisitionRequired = 7,
    Downloading = 8,
    Verifying = 9,
    Installing = 10,
    Failed = 11
}

/// <summary>Store-aware game launch workflow state.</summary>
public enum GameLaunchPhase
{
    Idle = 0,
    Starting = 1,
    PreparingEnhancements = 2,
    Launching = 3,
    LauncherInvoked = 4,
    Running = 5,
    LaunchFailed = 6
}

/// <summary>Explicit eight-state diagnostic model for Neural Rendering activation.</summary>
public enum NeuralRenderingActivationState
{
    Available = 0,
    DependencyMissing = 1,
    RuntimeMissing = 2,
    ReadyToConfigure = 3,
    InstallationInProgress = 4,
    Active = 5,
    RuntimeFailed = 6,
    Unsupported = 7
}

/// <summary>Selectable DLSS Neural Rendering model variant.</summary>
public enum NeuralRenderingModelType
{
    CC = 0,
    HT = 1
}

/// <summary>Detailed static validation result for user-imported NVIDIA NGX DLLs.</summary>
public enum DllValidationStatus
{
    NotImported = 0,
    Compatible = 1,
    ImportedSuccessfully = 2,
    VersionTooOld = 3,
    WrongArchitecture = 4,
    InvalidDll = 5,
    CorruptedFile = 6,
    UnsupportedModel = 7,
    SignatureVerificationUnavailable = 8,
    FailedValidation = 9
}

