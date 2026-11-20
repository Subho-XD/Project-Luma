using System.Text.Json.Serialization;
using ProjectLuma.Core.Enums;

namespace ProjectLuma.Core.Models;

/// <summary>Structured non-blocking progress snapshot for component downloads and staging.</summary>
public sealed record PackageOperationProgress(
    ComponentInstallState State,
    double Percent,
    long DownloadedBytes,
    long TotalBytes,
    double SpeedBytesPerSec,
    string Message);

/// <summary>Manifest entry for a registered upstream rendering backend.</summary>
public sealed class BackendManifest
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FeatureCategory { get; set; } = string.Empty;
    public string UpstreamRepository { get; set; } = string.Empty;
    public string OfficialReleaseApiUrl { get; set; } = string.Empty;
    public string OfficialReleasesPageUrl { get; set; } = string.Empty;
    public string CurrentInstalledVersion { get; set; } = "Not Installed";
    public string PinnedStableVersion { get; set; } = string.Empty;
    public string PinnedExperimentalVersion { get; set; } = string.Empty;
    public string LatestDiscoveredVersion { get; set; } = string.Empty;
    public string ReleaseDateUtc { get; set; } = string.Empty;
    public List<string> SupportedArchitectures { get; set; } = new();
    public List<string> SupportedApis { get; set; } = new();
    public List<string> SupportedFeatures { get; set; } = new();
    public List<string> Rdna34Features { get; set; } = new();
    public List<string> Rdna2Features { get; set; } = new();
    public List<string> RequiredDependencies { get; set; } = new();
    public List<string> RequiredUserDlls { get; set; } = new();
    public string MinimumAppVersion { get; set; } = "1.0.0";
    public string CompatibilityStatus { get; set; } = string.Empty;
    public string LicenseSpdx { get; set; } = string.Empty;
    public bool LicenseRedistributionAllowed { get; set; }
    public List<string> AssetPatterns { get; set; } = new();
    public string PreferredInstallMethod { get; set; } = string.Empty;
    public string ExpectedSha256 { get; set; } = string.Empty;
    public VerificationLevel VerificationLevel { get; set; } = VerificationLevel.OfficialHttpsReducedVerification;
    public string InstallationInstructions { get; set; } = string.Empty;
    public string FriendlyDescription { get; set; } = string.Empty;
    public string SupportedGamesSummary { get; set; } = string.Empty;
    public string RedistributionPolicy { get; set; } = "permitted"; // permitted, restricted, driver-managed, bundled-with-parent
    public string ParentComponentId { get; set; } = string.Empty;
    public string PublisherSignature { get; set; } = string.Empty;
    public string ReleaseChannel { get; set; } = "stable";
    public List<string> AdvancedFileNames { get; set; } = new();
    public string StagedInstallPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool UpdateAvailable =>
        !string.IsNullOrWhiteSpace(LatestDiscoveredVersion) &&
        !string.Equals(CurrentInstalledVersion, LatestDiscoveredVersion, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(CurrentInstalledVersion, "Not Installed", StringComparison.OrdinalIgnoreCase);

    // Runtime-only Intelligent Component Manager status (not serialized into seed JSON)
    [JsonIgnore] public ComponentInstallState InstallState { get; set; } = ComponentInstallState.RequiresSetup;
    [JsonIgnore] public string InstallStateLabel { get; set; } = "Ready to install";
    [JsonIgnore] public bool IsBusy { get; set; }
    [JsonIgnore] public bool CanDownload { get; set; }
    [JsonIgnore] public double DownloadProgressPercent { get; set; }
    [JsonIgnore] public string DownloadSpeedText { get; set; } = string.Empty;
    [JsonIgnore] public string DownloadedSizeText { get; set; } = string.Empty;
    [JsonIgnore] public string StatusDetailText { get; set; } = string.Empty;
    [JsonIgnore] public string LastErrorText { get; set; } = string.Empty;
}

/// <summary>Live release metadata fetched from the official GitHub Releases API.</summary>
public sealed class BackendReleaseInfo
{
    public string BackendId { get; set; } = string.Empty;
    public string TagName { get; set; } = string.Empty;
    public string ReleaseName { get; set; } = string.Empty;
    public bool IsPrerelease { get; set; }
    public DateTime PublishedAtUtc { get; set; }
    public string AssetName { get; set; } = string.Empty;
    public string AssetDownloadUrl { get; set; } = string.Empty;
    public long AssetSizeBytes { get; set; }
    public string AssetExpectedSha256 { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public string HtmlUrl { get; set; } = string.Empty;
}

/// <summary>Metadata for a user-owned NVIDIA NGX DLL imported into Project Luma's local vault.</summary>
public sealed class UserImportedDllRecord
{
    public string FileName { get; set; } = string.Empty;
    public string DllKind { get; set; } = string.Empty; // nvngx_dlssnr, nvngx_dlss, nvngx_dlssg, nvngx_dlssd
    public string OriginalSourcePath { get; set; } = string.Empty;
    public string FileVersion { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public bool IsPe64Valid { get; set; }
    public string Architecture { get; set; } = "x64";
    public bool HasAuthenticSignature { get; set; }
    public string SignerSubject { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public bool SupportsCcModel { get; set; }
    public bool SupportsHtModel { get; set; }
    public List<string> CompatibleModels { get; set; } = new();
    public string BackendCompatibility { get; set; } = "DLSS-NR-on-AMD v0.9.0";
    public DllValidationStatus ValidationState { get; set; } = DllValidationStatus.ImportedSuccessfully;
    public string ValidationMessage { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class NeuralModelRequirement
{
    public string ModelKey { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RequiredDll { get; set; } = "nvngx_dlssnr.dll";
    public string MinimumVersion { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class NeuralCompanionDllRequirement
{
    public string FileName { get; set; } = string.Empty;
    public string DllKind { get; set; } = string.Empty;
    public string Feature { get; set; } = string.Empty;
    public string MinimumVersion { get; set; } = "3.5.0.0";
    public bool Rdna2Supported { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class NeuralRenderingRequirementsManifest
{
    public string SchemaVersion { get; set; } = "1.0.0";
    public string BackendId { get; set; } = "dlss-nr-on-amd";
    public string BackendVersion { get; set; } = "v0.9.0";
    public string OfficialReleaseUrl { get; set; } = "https://github.com/danielblnc/DLSS-NR-on-AMD/releases/tag/v0.9.0";
    public string LicenseUrl { get; set; } = "https://github.com/danielblnc/DLSS-NR-on-AMD/blob/master/LICENSE";
    public bool RedistributionAllowed { get; set; }
    public string IntegrationMode { get; set; } = "NeuralHubImportHandoff";
    public string IntegrationNotes { get; set; } = string.Empty;
    public Dictionary<string, NeuralModelRequirement> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<NeuralCompanionDllRequirement> OptionalCompanionDlls { get; set; } = new();
    public string Rdna2Notes { get; set; } = string.Empty;

    public NeuralModelRequirement GetModelRequirement(NeuralRenderingModelType model)
    {
        string key = model.ToString();
        if (Models.TryGetValue(key, out var req)) return req;
        return model == NeuralRenderingModelType.CC
            ? new NeuralModelRequirement { ModelKey = "CC", DisplayName = "CC Model (Fastest)", RequiredDll = "nvngx_dlssnr.dll", MinimumVersion = "310.9.16.0" }
            : new NeuralModelRequirement { ModelKey = "HT", DisplayName = "HT Model (Standard)", RequiredDll = "nvngx_dlssnr.dll", MinimumVersion = "310.8.0.0" };
    }
}

public sealed class DllValidationReport
{
    public string FileName { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DllKind { get; set; } = string.Empty;
    public NeuralRenderingModelType TargetModel { get; set; } = NeuralRenderingModelType.CC;
    public string MinimumRequiredVersion { get; set; } = string.Empty;
    public string DetectedVersion { get; set; } = string.Empty;
    public bool IsReadable { get; set; }
    public bool IsPeValid { get; set; }
    public bool Is64Bit { get; set; }
    public string ArchitectureText { get; set; } = "Unknown";
    public long FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool HasAuthenticSignature { get; set; }
    public string SignerSubject { get; set; } = string.Empty;
    public string SignatureLimitationNote { get; set; } = string.Empty;
    public bool SupportsCcModel { get; set; }
    public bool SupportsHtModel { get; set; }
    public string BackendCompatibilityText { get; set; } = string.Empty;
    public DllValidationStatus Status { get; set; } = DllValidationStatus.FailedValidation;
    public string StatusLabel { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool IsAcceptableForImport =>
        Status is DllValidationStatus.Compatible or DllValidationStatus.ImportedSuccessfully or DllValidationStatus.SignatureVerificationUnavailable;
}

public sealed class DiscoveredDllCandidate
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceCategory { get; set; } = string.Empty;
    public DllValidationReport Validation { get; set; } = new();
    public bool CanImport => Validation.IsAcceptableForImport;
    public string DisplaySummary =>
        $"{Validation.FileName} · v{(string.IsNullOrWhiteSpace(Validation.DetectedVersion) ? "Unknown" : Validation.DetectedVersion)} · {SourceCategory} · {Validation.StatusLabel}";
}

public sealed class AiVisualsSetupResult
{
    public bool IsReadyToEnable { get; set; }
    public string BlockerSummary { get; set; } = string.Empty;
    public NeuralHubDetectionResultInfo? NeuralHub { get; set; }
    public List<DiscoveredDllCandidate> DiscoveredCandidates { get; set; } = new();
    public ComponentResolutionPlan? Plan { get; set; }
}

public sealed class NeuralHubDetectionResultInfo
{
    public bool IsDetected { get; set; }
    public string InstallDirectory { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

/// <summary>Per-game NeuralHub integration, stub loader, configuration, and OptiScaler conflict snapshot.</summary>
public sealed class NeuralHubGameIntegrationStatus
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string GameDirectory { get; set; } = string.Empty;
    public string ExecutableName { get; set; } = string.Empty;
    public string RegistryKey { get; set; } = string.Empty;
    public string ExpectedProxyDll { get; set; } = string.Empty;
    public string ProxyDllPath { get; set; } = string.Empty;
    public long ProxyDllSizeBytes { get; set; }
    public bool IsRegisteredInNeuralHub { get; set; }
    public bool IsGameLoaderInstalled { get; set; }
    public bool HasOptiScalerProxyConflict { get; set; }
    public bool IsGameConfigured { get; set; }
    public bool HasRuntimeLogInitialized { get; set; }
    public string RuntimeLogPath { get; set; } = string.Empty;
    public string ConfigIniPath { get; set; } = string.Empty;
    public string StatusSummary { get; set; } = string.Empty;
    public string ConflictDetail { get; set; } = string.Empty;
}

/// <summary>Complete read-only diagnostic state across NeuralHub app, runtime, DLL store, Vault sync, and games.</summary>
public sealed class NeuralHubStatusReport
{
    public bool IsAppDetected { get; set; }
    public string DetectionSource { get; set; } = string.Empty;
    public string StorePath { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;

    public bool IsRuntimeVerified { get; set; }
    public string RuntimePath { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public string RuntimeAbi { get; set; } = string.Empty;
    public bool HasPayloadMarker { get; set; }
    public bool HasHostDll { get; set; }
    public bool HasBundledFsrUpscaler { get; set; }
    public bool HasBundledFsrFrameGen { get; set; }

    public bool IsDllImportedInNeuralHub { get; set; }
    public string NeuralHubDllFileName { get; set; } = string.Empty;
    public string NeuralHubDllFullPath { get; set; } = string.Empty;
    public string NeuralHubDllVersion { get; set; } = string.Empty;
    public string NeuralHubDllSha256 { get; set; } = string.Empty;
    public string NeuralHubSupportedModels { get; set; } = string.Empty;

    public bool IsDllImportedInLumaVault { get; set; }
    public string LumaVaultDllPath { get; set; } = string.Empty;
    public string LumaVaultDllVersion { get; set; } = string.Empty;
    public string LumaVaultDllSha256 { get; set; } = string.Empty;
    public bool IsVaultSynchronizedWithNeuralHub { get; set; }

    public string StatusHeadline { get; set; } = "NeuralHub not detected";
    public string DllSyncSummary { get; set; } = string.Empty;
    public string DiagnosticsSummary { get; set; } = string.Empty;
    public List<NeuralHubGameIntegrationStatus> RegisteredGames { get; set; } = new();
}

/// <summary>Optional overrides allowing tests to isolate or mock NeuralHub host discovery deterministically.</summary>
public sealed class NeuralHubProbeOptions
{
    public static NeuralHubProbeOptions Disabled { get; } = new() { DisableHostProbe = true };

    public bool DisableHostProbe { get; set; }
    public bool IncludeRegistry { get; set; } = true;
    public bool IncludeRunningProcesses { get; set; } = true;
    public string? ExplicitRegistryStorePath { get; set; }
    public string? ExplicitRegistryRuntimePath { get; set; }
    public string? ExplicitRegistryRuntimeVersion { get; set; }
    public string? ExplicitRegistryRuntimeAbi { get; set; }
    public List<string> CandidateDirectories { get; set; } = new();
}

/// <summary>A single file deployment operation in a transactional backend installation plan.</summary>
public sealed class BackendFileDeployItem
{
    public string SourcePath { get; set; } = string.Empty;
    public string RelativeTargetPath { get; set; } = string.Empty;
    public string ExpectedSha256 { get; set; } = string.Empty;
    public bool IsIniMerge { get; set; }
    public Dictionary<string, Dictionary<string, string>> IniSections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Complete transactional plan generated by rendering adapters for GameInstaller.</summary>
public sealed class BackendInstallPlan
{
    public string BackendId { get; set; } = string.Empty;
    public string BackendVersion { get; set; } = string.Empty;
    public List<RenderingFeatureType> Features { get; set; } = new();
    public List<BackendFileDeployItem> FilesToDeploy { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>Resolved cover, hero, logo, and icon asset paths for a discovered game.</summary>
public sealed class GameArtworkSet
{
    public string CoverImagePath { get; set; } = string.Empty;
    public string HeroImagePath { get; set; } = string.Empty;
    public string LogoImagePath { get; set; } = string.Empty;
    public string IconImagePath { get; set; } = string.Empty;
    public ArtworkSourceKind Source { get; set; } = ArtworkSourceKind.None;
}

/// <summary>Single step in the beginner-friendly "Set Up This Game" progress workflow.</summary>
public sealed class SetupProgressStep
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public SetupStepState State { get; set; } = SetupStepState.Pending;
    public string Detail { get; set; } = string.Empty;
    public string StateDisplay => State switch
    {
        SetupStepState.Complete => "Complete",
        SetupStepState.InProgress => "In Progress",
        SetupStepState.AttentionRequired => "Attention Required",
        SetupStepState.Failed => "Failed",
        _ => "Pending"
    };
}

/// <summary>Component dependency verdict evaluated by the Intelligent Component Manager.</summary>
public sealed class ComponentRequirementItem
{
    public string ComponentId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string PlainEnglishPurpose { get; set; } = string.Empty;
    public bool IsRequiredForSelectedProfile { get; set; }
    public bool IsSatisfied { get; set; }
    public bool CanDownloadAutomatically { get; set; }
    public string OfficialGuidanceUrl { get; set; } = string.Empty;
    public string ActionPrompt { get; set; } = string.Empty;
    public string TechnicalDetail { get; set; } = string.Empty;
}

public sealed class ComponentResolutionPlan
{
    public string BackendId { get; set; } = string.Empty;
    public bool ReadyToInstallImmediately { get; set; }
    public bool RequiresUserAttention { get; set; }
    public string SummaryBanner { get; set; } = string.Empty;
    public List<ComponentRequirementItem> Components { get; set; } = new();
}
