using System.IO;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Models;

namespace ProjectLuma.UI.ViewModels;

/// <summary>Observable Enhancement Library component row with disk-verified status, 4-category grouping, and non-blocking streaming download progress.</summary>
public sealed class BackendComponentItem : ObservableObject
{
    private CancellationTokenSource? _downloadCts;
    private ComponentInstallState _installState;
    private string _installStateLabel = string.Empty;
    private bool _isBusy;
    private bool _canDownload;
    private bool _isDriverManaged;
    private bool _isHardwareIncompatible;
    private bool _isExternalAcquisition;
    private bool _isBundledWithParent;
    private bool _isDetailsExpanded;
    private double _downloadProgressPercent;
    private string _downloadSpeedText = string.Empty;
    private string _downloadedSizeText = string.Empty;
    private string _statusDetailText = string.Empty;
    private string _lastErrorText = string.Empty;
    private string _effectiveStagedPath = string.Empty;
    private string _effectiveInstalledVersion = string.Empty;
    private bool _isInstalledOnDisk;

    public BackendManifest Manifest { get; }

    public BackendComponentItem(BackendManifest manifest, DetectedGpuInfo gpu, BackendManifest? parentManifest = null)
    {
        Manifest = manifest;
        RefreshStateFromManifest(gpu, parentManifest);
    }

    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public bool IsNeuralRenderingComponent => Id.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase);
    public string CategoryKey => Manifest.FeatureCategory;
    public string CategoryDisplayName => Manifest.FeatureCategory switch
    {
        "NeuralRendering" => "AI Visual Enhancement",
        "SuperResolution" => "Super Resolution",
        "FrameGeneration" => "Frame Generation",
        "Reconstruction" => "Reconstruction",
        _ => Manifest.FeatureCategory
    };
    public string LicenseSpdx => Manifest.LicenseSpdx;
    public string FriendlyDescription => string.IsNullOrWhiteSpace(Manifest.FriendlyDescription) ? Manifest.CompatibilityStatus : Manifest.FriendlyDescription;
    public string SupportedGamesSummary => string.IsNullOrWhiteSpace(Manifest.SupportedGamesSummary)
        ? string.Join(", ", Manifest.SupportedApis)
        : Manifest.SupportedGamesSummary;
    public string GpuSupportSummary => Manifest.SupportedArchitectures.Count > 0
        ? string.Join(", ", Manifest.SupportedArchitectures)
        : "All AMD RDNA";
    public string MissingDependenciesSummary => Manifest.RequiredUserDlls.Count > 0
        ? string.Join("; ", Manifest.RequiredUserDlls)
        : (Manifest.RequiredDependencies.Count > 0 ? string.Join("; ", Manifest.RequiredDependencies) : "None");
    public string AdvancedFilesSummary => Manifest.AdvancedFileNames.Count > 0
        ? string.Join(", ", Manifest.AdvancedFileNames)
        : string.Join(", ", Manifest.AssetPatterns);
    public string CompatibilityStatus => Manifest.CompatibilityStatus;
    public string CurrentInstalledVersion => string.IsNullOrWhiteSpace(_effectiveInstalledVersion) ? Manifest.CurrentInstalledVersion : _effectiveInstalledVersion;
    public string OfficialReleasesPageUrl => Manifest.OfficialReleasesPageUrl;
    public bool IsBundledWithParent => _isBundledWithParent;
    public bool CanRepairOrVerify => IsInstalled && !IsDriverManaged && !IsHardwareIncompatible && !IsBusy;
    public bool CanRemove => IsInstalled && !IsDriverManaged && !IsHardwareIncompatible && !IsBundledWithParent && !IsBusy;

    public bool IsInstalled => _isInstalledOnDisk;

    private static bool ComputeIsInstalledOnDisk(string backendId, string? pathToCheck, string installedVersion)
    {
        if (string.IsNullOrWhiteSpace(pathToCheck) ||
            !Directory.Exists(pathToCheck) ||
            string.IsNullOrWhiteSpace(installedVersion) ||
            installedVersion.Equals("Not Installed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (backendId.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase))
            return ProjectLuma.PackageManager.NeuralHubDetector.HasRuntimeBinaryPayload(pathToCheck);

        return Directory.EnumerateFiles(pathToCheck, "*", SearchOption.AllDirectories)
            .Any(f => !Path.GetFileName(f).Equals("luma-package-meta.json", StringComparison.OrdinalIgnoreCase));
    }

    public ComponentInstallState InstallState
    {
        get => _installState;
        set
        {
            if (SetProperty(ref _installState, value))
                OnPropertyChanged(nameof(IsFailed));
        }
    }

    public bool IsFailed => InstallState == ComponentInstallState.Failed;
    public string InstallStateLabel { get => _installStateLabel; set => SetProperty(ref _installStateLabel, value); }
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanRepairOrVerify));
                OnPropertyChanged(nameof(CanRemove));
            }
        }
    }
    public bool CanDownload { get => _canDownload; set => SetProperty(ref _canDownload, value); }
    public bool IsDriverManaged { get => _isDriverManaged; set => SetProperty(ref _isDriverManaged, value); }
    public bool IsHardwareIncompatible { get => _isHardwareIncompatible; set => SetProperty(ref _isHardwareIncompatible, value); }
    public bool IsExternalAcquisition { get => _isExternalAcquisition; set => SetProperty(ref _isExternalAcquisition, value); }
    public bool IsDetailsExpanded { get => _isDetailsExpanded; set => SetProperty(ref _isDetailsExpanded, value); }
    public double DownloadProgressPercent { get => _downloadProgressPercent; set => SetProperty(ref _downloadProgressPercent, value); }
    public string DownloadSpeedText { get => _downloadSpeedText; set => SetProperty(ref _downloadSpeedText, value); }
    public string DownloadedSizeText { get => _downloadedSizeText; set => SetProperty(ref _downloadedSizeText, value); }
    public string StatusDetailText { get => _statusDetailText; set => SetProperty(ref _statusDetailText, value); }
    public string LastErrorText { get => _lastErrorText; set => SetProperty(ref _lastErrorText, value); }
    public string ActionButtonLabel => IsFailed
        ? "Retry"
        : (InstallState == ComponentInstallState.UpdateAvailable
            ? "Update"
            : (IsInstalled ? "Reinstall" : (IsBundledWithParent ? "Install via OptiScaler" : "Install")));

    public void RefreshStateFromManifest(DetectedGpuInfo gpu, BackendManifest? parentManifest = null)
    {
        bool archSupported = Manifest.SupportedArchitectures.Count == 0 ||
                             Manifest.SupportedArchitectures.Contains(gpu.Architecture.ToString(), StringComparer.OrdinalIgnoreCase);

        _isBundledWithParent = Manifest.PreferredInstallMethod.Equals("BundledWithParent", StringComparison.OrdinalIgnoreCase) &&
                               parentManifest != null;
        if (_isBundledWithParent && parentManifest != null)
        {
            _effectiveStagedPath = parentManifest.StagedInstallPath;
            bool parentInstalled = ComputeIsInstalledOnDisk(parentManifest.Id, parentManifest.StagedInstallPath, parentManifest.CurrentInstalledVersion);
            _effectiveInstalledVersion = parentInstalled ? parentManifest.CurrentInstalledVersion : "Not Installed";
            _isInstalledOnDisk = parentInstalled;
        }
        else
        {
            _effectiveStagedPath = Manifest.StagedInstallPath;
            _effectiveInstalledVersion = Manifest.CurrentInstalledVersion;
            _isInstalledOnDisk = ComputeIsInstalledOnDisk(Manifest.Id, _effectiveStagedPath, _effectiveInstalledVersion);
        }

        if (!archSupported)
        {
            InstallState = ComponentInstallState.HardwareIncompatible;
            InstallStateLabel = "Not compatible";
            IsHardwareIncompatible = true;
            CanDownload = false;
            IsDriverManaged = false;
            IsExternalAcquisition = false;
            StatusDetailText = $"Not compatible with {gpu.ModelName} ({gpu.Architecture}).";
        }
        else if (Manifest.PreferredInstallMethod.Equals("GuidedDriverAdvisor", StringComparison.OrdinalIgnoreCase))
        {
            InstallState = ComponentInstallState.DriverManaged;
            InstallStateLabel = "Driver managed";
            IsDriverManaged = true;
            CanDownload = false;
            IsHardwareIncompatible = false;
            IsExternalAcquisition = false;
            StatusDetailText = "Configured directly in AMD Software: Adrenalin Edition.";
        }
        else if (!Manifest.LicenseRedistributionAllowed)
        {
            IsExternalAcquisition = true;
            CanDownload = false;
            IsDriverManaged = false;
            IsHardwareIncompatible = false;
            if (IsInstalled)
            {
                InstallState = Manifest.ReleaseChannel.Equals("experimental", StringComparison.OrdinalIgnoreCase) && gpu.Architecture == GpuArchitecture.RDNA2
                    ? ComponentInstallState.Experimental
                    : ComponentInstallState.Installed;
                InstallStateLabel = InstallState == ComponentInstallState.Experimental ? "Experimental" : "Installed";
                StatusDetailText = $"Connected local installation ({CurrentInstalledVersion}).";
            }
            else
            {
                InstallState = ComponentInstallState.RequiresSetup;
                InstallStateLabel = "Requires attention";
                StatusDetailText = "License forbids bundling. Open Official Release or locate your local copy; Project Luma watches Downloads automatically.";
            }
        }
        else
        {
            IsExternalAcquisition = false;
            IsDriverManaged = false;
            IsHardwareIncompatible = false;
            CanDownload = true;

            if (Manifest.UpdateAvailable || (_isBundledWithParent && parentManifest?.UpdateAvailable == true))
            {
                InstallState = ComponentInstallState.UpdateAvailable;
                InstallStateLabel = "Update available";
                string nextVer = _isBundledWithParent && parentManifest != null ? parentManifest.LatestDiscoveredVersion : Manifest.LatestDiscoveredVersion;
                StatusDetailText = $"Installed {CurrentInstalledVersion} -> {nextVer} available.";
            }
            else if (IsInstalled)
            {
                InstallState = ComponentInstallState.Installed;
                InstallStateLabel = "Installed";
                StatusDetailText = _isBundledWithParent
                    ? $"Ready inside verified OptiScaler {CurrentInstalledVersion} package."
                    : $"Verified on disk ({CurrentInstalledVersion}).";
            }
            else
            {
                InstallState = ComponentInstallState.ReadyToInstall;
                InstallStateLabel = "Ready to install";
                StatusDetailText = _isBundledWithParent
                    ? "Installed automatically via the authorized OptiScaler package."
                    : "Ready for authorized GitHub download and SHA-256 verification.";
            }
        }

        OnPropertyChanged(nameof(CurrentInstalledVersion));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(IsBundledWithParent));
        OnPropertyChanged(nameof(ActionButtonLabel));
        OnPropertyChanged(nameof(CanRepairOrVerify));
        OnPropertyChanged(nameof(CanRemove));
    }

    public CancellationToken BeginOperation()
    {
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();
        IsBusy = true;
        LastErrorText = string.Empty;
        DownloadProgressPercent = 0;
        DownloadSpeedText = string.Empty;
        DownloadedSizeText = string.Empty;
        InstallState = ComponentInstallState.Downloading;
        InstallStateLabel = "Setting up";
        OnPropertyChanged(nameof(ActionButtonLabel));
        return _downloadCts.Token;
    }

    public void CancelOperation()
    {
        _downloadCts?.Cancel();
    }

    public void ApplyProgress(PackageOperationProgress p)
    {
        InstallState = p.State;
        InstallStateLabel = p.State switch
        {
            ComponentInstallState.Downloading or ComponentInstallState.Verifying or ComponentInstallState.Installing => "Setting up",
            ComponentInstallState.Installed => "Installed",
            ComponentInstallState.Failed => "Requires attention",
            _ => InstallStateLabel
        };
        DownloadProgressPercent = p.Percent;
        DownloadSpeedText = p.SpeedBytesPerSec > 0
            ? $"{(p.SpeedBytesPerSec / (1024d * 1024d)):0.0} MB/s"
            : string.Empty;
        DownloadedSizeText = p.TotalBytes > 0
            ? $"{(p.DownloadedBytes / (1024d * 1024d)):0.1} / {(p.TotalBytes / (1024d * 1024d)):0.1} MB"
            : p.DownloadedBytes > 0 ? $"{(p.DownloadedBytes / (1024d * 1024d)):0.1} MB" : string.Empty;
        StatusDetailText = p.Message;
    }

    public void CompleteOperation(bool succeeded, string? errorMessage = null)
    {
        IsBusy = false;
        _downloadCts?.Dispose();
        _downloadCts = null;

        if (!succeeded && !string.IsNullOrWhiteSpace(errorMessage))
        {
            InstallState = ComponentInstallState.Failed;
            InstallStateLabel = "Requires attention";
            LastErrorText = errorMessage;
            StatusDetailText = "Setup failed. Review error details and retry.";
        }
        else if (succeeded)
        {
            _effectiveStagedPath = Manifest.StagedInstallPath;
            _effectiveInstalledVersion = Manifest.CurrentInstalledVersion;
            _isInstalledOnDisk = ComputeIsInstalledOnDisk(Manifest.Id, _effectiveStagedPath, _effectiveInstalledVersion);
        }
        OnPropertyChanged(nameof(CurrentInstalledVersion));
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(ActionButtonLabel));
        OnPropertyChanged(nameof(CanRepairOrVerify));
        OnPropertyChanged(nameof(CanRemove));
    }

    public async Task RefreshStateFromManifestAsync(DetectedGpuInfo gpu, BackendManifest? parentManifest = null)
    {
        bool bundled = Manifest.PreferredInstallMethod.Equals("BundledWithParent", StringComparison.OrdinalIgnoreCase) && parentManifest != null;
        string targetId = bundled && parentManifest != null ? parentManifest.Id : Manifest.Id;
        string targetPath = bundled && parentManifest != null ? parentManifest.StagedInstallPath : Manifest.StagedInstallPath;
        string targetVer = bundled && parentManifest != null ? parentManifest.CurrentInstalledVersion : Manifest.CurrentInstalledVersion;

        bool installed = await Task.Run(() => ComputeIsInstalledOnDisk(targetId, targetPath, targetVer));
        _isInstalledOnDisk = installed;
        RefreshStateFromManifest(gpu, parentManifest);
    }
}
