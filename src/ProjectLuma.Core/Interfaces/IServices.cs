using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Models;

namespace ProjectLuma.Core.Interfaces;

public interface ILumaLogger
{
    event Action<LogEntryRecord>? EntryLogged;
    IReadOnlyList<LogEntryRecord> RecentEntries { get; }
    string LogDirectory { get; }
    void Log(LumaLogLevel level, string category, string message, string? details = null);
    void Info(string category, string message, string? details = null);
    void Warn(string category, string message, string? details = null);
    void Error(string category, string message, Exception? ex = null);
}

public interface IGpuDetector
{
    Task<DetectedGpuInfo> DetectPrimaryGpuAsync(CancellationToken ct = default);
    IReadOnlyList<GpuCatalogEntry> GetCatalogEntries();
}

public interface ICapabilityEngine
{
    HardwareCapabilityProfile EvaluateHardware(
        DetectedGpuInfo gpu,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        IReadOnlyCollection<BackendManifest> manifests,
        UpdateChannel channel);

    GameEnhancementProfile EvaluateGame(
        DiscoveredGame game,
        DetectedGpuInfo gpu,
        IReadOnlyCollection<UserImportedDllRecord> userDlls,
        IReadOnlyCollection<BackendManifest> manifests,
        CompatibilitySeedProfile? seedProfile,
        UpdateChannel channel);
}

public interface ICompatibilityRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CompatibilitySeedProfile>> GetSeedProfilesAsync(CancellationToken ct = default);
    CompatibilitySeedProfile? FindSeedForGame(DiscoveredGame game);
    Task SaveDiscoveredGamesAsync(IEnumerable<DiscoveredGame> games, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredGame>> LoadDiscoveredGamesAsync(CancellationToken ct = default);
    Task SaveGameProfileAsync(GameEnhancementProfile profile, CancellationToken ct = default);
    Task<GameEnhancementProfile?> LoadGameProfileAsync(string gameId, CancellationToken ct = default);
    Task SaveHardwareProfileAsync(HardwareCapabilityProfile profile, CancellationToken ct = default);
    Task<AppSettingsModel> LoadSettingsAsync(CancellationToken ct = default);
    Task SaveSettingsAsync(AppSettingsModel settings, CancellationToken ct = default);
    Task SaveBackendManifestsAsync(IEnumerable<BackendManifest> manifests, CancellationToken ct = default);
    Task<IReadOnlyList<BackendManifest>> LoadBackendManifestsAsync(CancellationToken ct = default);
    Task SaveUserDllAsync(UserImportedDllRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<UserImportedDllRecord>> LoadUserDllsAsync(CancellationToken ct = default);
    Task RemoveUserDllAsync(string fileName, CancellationToken ct = default);
    Task RecordUpdateHistoryAsync(string componentId, string fromVer, string toVer, string status, string notes, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetUpdateHistoryAsync(int maxRows = 50, CancellationToken ct = default);
}

public interface IPeAnalyzer
{
    PeInspectionResult? InspectFile(string filePath);
}

public sealed class PeInspectionResult
{
    public bool Is64Bit { get; set; }
    public bool IsDll { get; set; }
    public List<string> ImportedDlls { get; set; } = new();
    public bool ImportsDll(string dllName) => ImportedDlls.Any(d => string.Equals(d, dllName, StringComparison.OrdinalIgnoreCase));
}

public interface IGameLibraryScanner
{
    Task<IReadOnlyList<DiscoveredGame>> ScanAllAsync(
        IEnumerable<string> customLibraryRoots,
        IProgress<GameLibraryScanProgress>? progress = null,
        CancellationToken ct = default);

    Task<DiscoveredGame?> InspectSingleDirectoryOrExeAsync(string path, GameLauncherType launcher = GameLauncherType.Manual, string storeAppId = "", CancellationToken ct = default);
}

public interface IUserDllVault
{
    string VaultDirectory { get; }
    string NeuralRenderingVaultDirectory { get; }
    NeuralRenderingRequirementsManifest GetNeuralRequirementsManifest();
    Task<IReadOnlyList<UserImportedDllRecord>> GetImportedDllsAsync(CancellationToken ct = default);
    Task<DllValidationReport> ValidateDllAsync(string filePath, NeuralRenderingModelType targetModel = NeuralRenderingModelType.CC, CancellationToken ct = default);
    Task<UserImportedDllRecord> ImportDllAsync(string sourceFilePath, CancellationToken ct = default);
    Task<UserImportedDllRecord> ImportDllAsync(string sourceFilePath, NeuralRenderingModelType targetModel, CancellationToken ct = default);
    Task<DllValidationReport> VerifyImportedDllAsync(string fileName, NeuralRenderingModelType targetModel = NeuralRenderingModelType.CC, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredDllCandidate>> DiscoverCandidateDllsAsync(
        IEnumerable<string> directoriesToScan,
        string sourceCategory,
        NeuralRenderingModelType targetModel = NeuralRenderingModelType.CC,
        int maxDepth = 3,
        CancellationToken ct = default);
    Task<IReadOnlyList<UserImportedDllRecord>> ScanGameLibrariesForUserDllsAsync(IEnumerable<string> directoriesToScan, CancellationToken ct = default);
    Task DeleteDllAsync(string fileName, CancellationToken ct = default);
}

public interface IPackageManager
{
    string StagingRootDirectory { get; }
    Task<IReadOnlyList<BackendManifest>> GetManifestsAsync(CancellationToken ct = default);
    Task<BackendReleaseInfo?> CheckLatestReleaseAsync(BackendManifest manifest, UpdateChannel channel, CancellationToken ct = default);
    Task<BackendManifest> DownloadAndStageAsync(BackendManifest manifest, BackendReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default);
    Task<BackendManifest> DownloadAndStageDetailedAsync(BackendManifest manifest, BackendReleaseInfo release, IProgress<PackageOperationProgress> progress, CancellationToken ct = default);
    Task<BackendManifest> StageLocalArchiveAsync(BackendManifest manifest, string archiveOrFolderPath, string versionLabel, CancellationToken ct = default);
    Task<bool> RollbackBackendAsync(string backendId, CancellationToken ct = default);
    Task<bool> VerifyBackendIntegrityAsync(string backendId, CancellationToken ct = default);
    Task RemoveBackendAsync(string backendId, CancellationToken ct = default);
}

public interface IRenderingBackendAdapter
{
    string BackendId { get; }
    RenderingFeatureType FeatureType { get; }
    BackendInstallPlan BuildInstallPlan(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        string stagedBackendDirectory,
        IReadOnlyCollection<UserImportedDllRecord> userDlls);
}

public interface IBackendOrchestrator
{
    IReadOnlyList<IRenderingBackendAdapter> Adapters { get; }
    BackendInstallPlan ComposeCombinedPlan(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        IReadOnlyCollection<BackendManifest> manifests,
        IReadOnlyCollection<UserImportedDllRecord> userDlls);
    IReadOnlyList<UpscalingQualityPreset> GetSupportedPresets(string upscalerBackendId, string upscalerEngine);
    IReadOnlyList<NeuralQualityMode> GetSupportedNeuralModes(GpuArchitecture architecture);
    IReadOnlyList<string> GetSupportedUpscalerEngines(GpuArchitecture architecture);
    IReadOnlyList<(string Id, string Name, CapabilityState State, string Reason)> GetSupportedFrameGenEngines(GpuArchitecture architecture, GraphicsApi api);
}

public interface IGameInstaller
{
    Task<InstallSessionManifest> InstallOrUpdateAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        BackendInstallPlan plan,
        CancellationToken ct = default);

    Task<bool> UninstallAsync(DiscoveredGame game, CancellationToken ct = default);
    Task<IntegrationHealthState> VerifyInstallationHealthAsync(DiscoveredGame game, CancellationToken ct = default);
    Task<IReadOnlyList<InstallSessionManifest>> GetBackupSessionsAsync(string gameExeDirectory, CancellationToken ct = default);
    IReadOnlyList<string> DetectRunningProcessesInDirectory(string rootDirectory);
}

public interface IUpdateCoordinator
{
    UpdateCoordinatorStatus Status { get; }
    event Action<UpdateCoordinatorStatus>? StatusChanged;
    Task CheckNowAsync(CancellationToken ct = default);
    Task ApplyBackendUpdateAsync(string backendId, CancellationToken ct = default);
    Task UpdateEligibleGamesForBackendAsync(string backendId, CancellationToken ct = default);
}

public interface IArtworkDiscoveryService
{
    string CacheDirectory { get; }
    Task<GameArtworkSet> ResolveArtworkAsync(DiscoveredGame game, bool allowRemoteLookup, CancellationToken ct = default);
    Task<GameArtworkSet> SetCustomArtworkAsync(DiscoveredGame game, string? customCoverPath, string? customHeroPath, CancellationToken ct = default);
    void SaveSteamGridDbApiKey(string? apiKey);
    bool HasSteamGridDbApiKey();
    void ClearCache();
}

public interface IIntelligentComponentResolver
{
    Task<ComponentResolutionPlan> AnalyzeRequirementsAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        DetectedGpuInfo gpu,
        CancellationToken ct = default);

    Task<bool> ExecuteGuidedSetupAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        DetectedGpuInfo gpu,
        IProgress<SetupProgressStep>? progress = null,
        CancellationToken ct = default);

    Task<AiVisualsSetupResult> ExecuteAiVisualsDiscoveryAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        DetectedGpuInfo gpu,
        IEnumerable<string>? registeredGameDirectories = null,
        IProgress<SetupProgressStep>? progress = null,
        CancellationToken ct = default);

    Task<UserImportedDllRecord> LocateComponentOnPcAsync(string filePath, CancellationToken ct = default);

    Task<int> AutoDiscoverDownloadedComponentsAsync(
        string? downloadsDirectory = null,
        IEnumerable<string>? additionalGameDirs = null,
        CancellationToken ct = default);
}

public interface IGameLaunchService
{
    Task<GameLaunchResult> LaunchGameAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        IProgress<GameLaunchPhase>? progress = null,
        CancellationToken ct = default);
}

public interface IWindowsStartupService
{
    string RegistryKeyPath { get; }
    string ValueName { get; }
    bool IsEnabled();
    string? GetRegisteredCommand();
    void Enable(string executablePath);
    void Disable();
    void UpdateExecutablePathIfEnabled(string newExecutablePath);
}
