using System.ComponentModel;
using System.Runtime.CompilerServices;
using ProjectLuma.Core.Enums;

namespace ProjectLuma.Core.Models;

/// <summary>Transactional backup manifest written before modifying any game directory.</summary>
public sealed class InstallSessionManifest
{
    public string SessionId { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public string GameExePath { get; set; } = string.Empty;
    public string GameExeDirectory { get; set; } = string.Empty;
    public string BackendId { get; set; } = string.Empty;
    public string BackendVersion { get; set; } = string.Empty;
    public List<RenderingFeatureType> EnabledFeatures { get; set; } = new();
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
    public List<BackedUpFileRecord> BackedUpOriginals { get; set; } = new();
    public List<InstalledFileRecord> InstalledFiles { get; set; } = new();
    public bool Completed { get; set; }
}

public sealed class BackedUpFileRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public string BackupFileName { get; set; } = string.Empty;
    public string OriginalSha256 { get; set; } = string.Empty;
}

public sealed class InstalledFileRecord
{
    public string RelativePath { get; set; } = string.Empty;
    public string InstalledSha256 { get; set; } = string.Empty;
    public bool WasMergedIni { get; set; }
    public bool ExistedBefore { get; set; }
}

/// <summary>User preferences persisted across restarts in SQLite + JSON.</summary>
public sealed class AppSettingsModel
{
    public List<string> CustomLibraryPaths { get; set; } = new();
    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadVerifiedUpdates { get; set; } = false;
    public bool AutoApplyBackendUpdates { get; set; } = false;
    public bool AutoUpdateGameIntegrations { get; set; } = false;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;
    public int UpdateCheckIntervalHours { get; set; } = 6;
    public string BackupRootPath { get; set; } = string.Empty;
    public int BackupRetentionCount { get; set; } = 5;
    public LumaLogLevel MinimumLogLevel { get; set; } = LumaLogLevel.Info;
    public bool LaunchOnWindowsStartup { get; set; } = false;
    public bool MinimizeToTray { get; set; } = false;
    public bool CompactDensity { get; set; } = false;
    public string AppVersion { get; set; } = "3.3.2";
    public bool HasCompletedOnboarding { get; set; } = false;
    public bool EnableRemoteArtworkLookup { get; set; } = true;
    public bool SteamGridDbKeyConfigured { get; set; } = false;
    public bool ShowInGameNotification { get; set; } = true;
    public bool PreserveBackupsOnUninstall { get; set; } = true;
}

/// <summary>Result returned by store-aware game launcher service.</summary>
public sealed class GameLaunchResult
{
    public bool Succeeded { get; set; }
    public GameLaunchPhase Phase { get; set; } = GameLaunchPhase.Idle;
    public string ActiveProfileName { get; set; } = "Standard";
    public string ActiveEnhancementsSummary { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Live status reported by the background Update Coordinator service.</summary>
public sealed class UpdateCoordinatorStatus
{
    public bool IsChecking { get; set; }
    public bool IsOffline { get; set; }
    public DateTime? LastSuccessfulCheckUtc { get; set; }
    public DateTime? NextScheduledCheckUtc { get; set; }
    public int RateLimitRemaining { get; set; } = 60;
    public string CurrentPhase { get; set; } = "Idle";
    public int ProgressPercent { get; set; }
    public UpdateCheckItem? ApplicationUpdateItem { get; set; }
    public List<UpdateCheckItem> Items { get; set; } = new();
}

public sealed class UpdateCheckItem : INotifyPropertyChanged
{
    private string _installedVersion = string.Empty;
    private string _availableVersion = string.Empty;
    private string _status = "Up to date";
    private string _stateBadgeText = "Installed";
    private AppUpdateState _updateState = AppUpdateState.Idle;
    private string _actionLabel = string.Empty;
    private string _reasonText = string.Empty;
    private string _releaseNotes = string.Empty;
    private string _publisherSignatureSummary = string.Empty;
    private string _verificationDetails = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _canApply;
    private bool _canRollback;
    private bool _isBusy;
    private double _downloadProgress;
    private string _downloadSpeedText = string.Empty;
    private string _downloadedSizeText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ComponentId { get; set; } = string.Empty;
    public string ComponentName { get; set; } = string.Empty;
    public string Kind { get; set; } = "Backend"; // Application, Backend, GameIntegration
    public VerificationLevel VerificationLevel { get; set; }
    public string LicenseSpdx { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;

    public string InstalledVersion { get => _installedVersion; set => SetField(ref _installedVersion, value); }
    public string AvailableVersion { get => _availableVersion; set => SetField(ref _availableVersion, value); }
    public string Status { get => _status; set => SetField(ref _status, value); }
    public string StateBadgeText { get => _stateBadgeText; set => SetField(ref _stateBadgeText, value); }

    public AppUpdateState UpdateState
    {
        get => _updateState;
        set
        {
            if (SetField(ref _updateState, value))
                OnPropertyChanged(nameof(UpdateStateLabel));
        }
    }

    public string UpdateStateLabel => UpdateState switch
    {
        AppUpdateState.CheckingForUpdates => "Checking for updates",
        AppUpdateState.AlreadyUpToDate => "Already up to date",
        AppUpdateState.UpdateAvailable => "Update available",
        AppUpdateState.Downloading => "Downloading",
        AppUpdateState.VerifyingPackage => "Verifying package",
        AppUpdateState.PreparingInstallation => "Preparing installation",
        AppUpdateState.Installing => "Installing",
        AppUpdateState.UpdateCompleted => "Update completed",
        AppUpdateState.UpdateFailed => "Update failed",
        _ => StateBadgeText
    };

    public string ActionLabel
    {
        get => _actionLabel;
        set
        {
            if (SetField(ref _actionLabel, value))
                OnPropertyChanged(nameof(ShowActionButton));
        }
    }

    public bool ShowActionButton => !string.IsNullOrWhiteSpace(_actionLabel);
    public string ReasonText { get => _reasonText; set => SetField(ref _reasonText, value); }

    public string ReleaseNotes
    {
        get => _releaseNotes;
        set
        {
            if (SetField(ref _releaseNotes, value))
                OnPropertyChanged(nameof(HasReleaseNotes));
        }
    }

    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(_releaseNotes);
    public string PublisherSignatureSummary { get => _publisherSignatureSummary; set => SetField(ref _publisherSignatureSummary, value); }
    public string VerificationDetails { get => _verificationDetails; set => SetField(ref _verificationDetails, value); }
    public string ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }
    public bool CanApply { get => _canApply; set => SetField(ref _canApply, value); }
    public bool CanRollback { get => _canRollback; set => SetField(ref _canRollback, value); }
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }
    public double DownloadProgress { get => _downloadProgress; set => SetField(ref _downloadProgress, value); }
    public string DownloadSpeedText { get => _downloadSpeedText; set => SetField(ref _downloadSpeedText, value); }
    public string DownloadedSizeText { get => _downloadedSizeText; set => SetField(ref _downloadedSizeText, value); }
    public string DownloadUrl { get; set; } = string.Empty;
    public string ExpectedSha256 { get; set; } = string.Empty;
    public string AssetName { get; set; } = string.Empty;
    public string ReleasePageUrl { get; set; } = string.Empty;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>Structured log record displayed in Settings/Logs and written to local log files.</summary>
public sealed class LogEntryRecord
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public LumaLogLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
}

/// <summary>Centralizes Project Luma app-data directory resolution with test/sandbox override support.</summary>
public static class LumaPaths
{
    public const string AppDataOverrideEnvVar = "PROJECT_LUMA_APPDATA";

    public static string AppDataDirectory
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable(AppDataOverrideEnvVar);
            if (!string.IsNullOrWhiteSpace(overridePath))
                return Path.GetFullPath(overridePath.Trim());

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProjectLuma");
        }
    }

    public static string GetSubdirectory(string name) => Path.Combine(AppDataDirectory, name);
}

/// <summary>Result produced after downloading, verifying, and staging an application self-update.</summary>
public sealed class AppSelfUpdateStageResult
{
    public bool Succeeded { get; set; }
    public string TargetVersion { get; set; } = string.Empty;
    public string StageDirectory { get; set; } = string.Empty;
    public string TargetInstallDirectory { get; set; } = string.Empty;
    public string AppExecutablePath { get; set; } = string.Empty;
    public string UpdaterExecutablePath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string PublisherSignatureSummary { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;
}
