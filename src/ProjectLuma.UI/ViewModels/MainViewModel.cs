using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using ProjectLuma.Compatibility;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using ProjectLuma.UI.Views;

namespace ProjectLuma.UI.ViewModels;

public enum LumaPage
{
    Home,
    MyGames,
    GameDetails,
    EnhancementLibrary,
    Downloads,
    Settings
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IGpuDetector _gpuDetector;
    private readonly ICapabilityEngine _capabilityEngine;
    private readonly ICompatibilityRepository _repo;
    private readonly IGameLibraryScanner _scanner;
    private readonly IArtworkDiscoveryService _artworkService;
    private readonly IPackageManager _packageManager;
    private readonly IUserDllVault _dllVault;
    private readonly IBackendOrchestrator _orchestrator;
    private readonly IIntelligentComponentResolver _componentResolver;
    private readonly IGameInstaller _installer;
    private readonly IGameLaunchService _launchService;
    private readonly IUpdateCoordinator _updateCoordinator;
    private readonly ILumaLogger _logger;
    private readonly IUserDialogService _dialogs;
    private readonly IWindowsStartupService _startupService;
    private readonly TrayLifecycleController _trayController;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _setupCts;
    private CancellationTokenSource? _neuralScanCts;
    private CancellationTokenSource? _toastCts;
    private FileSystemWatcher? _downloadsWatcher;
    private CancellationTokenSource? _downloadsDebounceCts;
    private readonly SemaphoreSlim _profileSaveLock = new(1, 1);
    private readonly object _setupStepsLock = new();
    private bool _isDownloadsWatcherActive;

    private LumaPage _activePage = LumaPage.Home;
    private DetectedGpuInfo _gpuInfo = new();
    private HardwareCapabilityProfile _hardwareProfile = new();
    private AppSettingsModel _settings = new();
    private UpdateCoordinatorStatus _updateStatus = new();
    private GameCardViewModel? _selectedGame;
    private GameCardViewModel? _featuredGame;
    private ComponentResolutionPlan? _selectedGameComponents;
    private string _homeFilterMode = "Featured";
    private string _selectedEnhancementCategory = "All Categories";

    private string _searchQuery = string.Empty;
    private string _selectedLibraryTab = "All Games";
    private string _selectedLauncherFilter = "All Platforms";
    private string _selectedSortOrder = "Title (A-Z)";

    private bool _isScanning;
    private int _scanProgressPercent;
    private string _scanStatusMessage = "Ready";

    private string _toastMessage = string.Empty;
    private bool _isToastVisible;
    private string _actionAlertMessage = string.Empty;

    private bool _isSetupDrawerOpen;
    private bool _isSetupRunning;
    private bool _setupSucceeded;
    private string _setupErrorMessage = string.Empty;
    private string _setupDrawerHeader = "Setting up game enhancements";
    private bool _isAiVisualsSetupDrawer;
    private string _windowsStartupStatusText = "Checking Windows startup status...";

    // Dedicated NVIDIA Neural Rendering Model section state
    private NeuralRenderingModelType _selectedNeuralImportModel = NeuralRenderingModelType.CC;
    private string _neuralMinimumVersionRequired = "310.9.16.0";
    private string _neuralDetectedVersion = "Not detected";
    private string _neuralImportStatusText = "Not imported";
    private string _neuralValidationStatusText = "Not imported";
    private string _neuralValidationDetailText = "Select or scan for a legitimate user-owned nvngx_dlssnr.dll file.";
    private string _neuralLocalInstallationStatusText = "Inspecting local NeuralHub & DLL Vault...";
    private bool _hasImportedNeuralDll;
    private bool _isNeuralHubDetected;
    private string _neuralHubExecutablePath = string.Empty;
    private bool _isNeuralCandidateScanBusy;
    private string _neuralCandidateScanSummary = string.Empty;

    private bool _isOnboardingVisible;
    private int _onboardingStepIndex = 1;
    private string _steamGridDbKeyInput = string.Empty;
    private int _gridColumns = 5;
    private volatile bool _disposed;

    public ObservableCollection<GameCardViewModel> AllGames { get; } = new();
    public ObservableCollection<GameCardViewModel> HomeStripGames { get; } = new();
    public ObservableCollection<GameCardViewModel> FilteredGames { get; } = new();
    public ObservableCollection<GameCoverRow> VirtualGameRows { get; } = new();
    public ObservableCollection<BackendManifest> Backends { get; } = new();
    public ObservableCollection<BackendComponentItem> ComponentItems { get; } = new();
    public ObservableCollection<BackendComponentItem> FilteredComponentItems { get; } = new();
    public ObservableCollection<UserImportedDllRecord> UserDlls { get; } = new();
    public ObservableCollection<UpdateCheckItem> UpdateItems { get; } = new();
    public ObservableCollection<InstallSessionManifest> SelectedGameBackups { get; } = new();
    public ObservableCollection<SetupProgressStep> SetupSteps { get; } = new();
    public ObservableCollection<DiscoveredDllCandidate> DiscoveredNeuralCandidates { get; } = new();
    public ObservableCollection<DiscoveredDllCandidate> DiscoveredSetupCandidates { get; } = new();
    public ObservableCollection<string> CustomLibraryPaths { get; } = new();
    public ObservableCollection<LogEntryRecord> RecentLogs { get; } = new();
    public ObservableCollection<string> UpdateHistoryRows { get; } = new();

    public IReadOnlyList<string> EnhancementCategoryTabs { get; } = new[]
    {
        "All Categories",
        "AI Visual Enhancement",
        "Super Resolution",
        "Frame Generation",
        "Reconstruction"
    };
    public IReadOnlyList<string> LibraryTabs { get; } = new[] { "All Games", "Ready to Enhance", "Enhanced", "Setup Required", "Unsupported" };
    public IReadOnlyList<string> LauncherFilters { get; } = new[] { "All Platforms", "Steam", "Epic Games", "GOG Galaxy", "Ubisoft Connect", "EA App", "Battle.net", "Xbox", "Manual" };
    public IReadOnlyList<string> SortOrders { get; } = new[] { "Title (A-Z)", "Enhanced First", "Platform" };

    public RelayCommand NavigateCommand { get; }
    public RelayCommand BackToGamesCommand { get; }
    public RelayCommand<GameCardViewModel?> OpenGameDetailsCommand { get; }
    public RelayCommand<GameCardViewModel?> SelectFeaturedGameCommand { get; }
    public RelayCommand<string?> SelectHomeFilterCommand { get; }
    public AsyncRelayCommand<GameCardViewModel?> EnhanceGameFromHomeCommand { get; }
    public RelayCommand<string?> SelectLibraryTabCommand { get; }
    public RelayCommand<string?> SelectPresetCommand { get; }

    public AsyncRelayCommand RescanLibrariesCommand { get; }
    public RelayCommand CancelScanCommand { get; }
    public AsyncRelayCommand AddGameManuallyCommand { get; }
    public AsyncRelayCommand AddCustomLibraryFolderCommand { get; }
    public AsyncRelayCommand<string?> RemoveLibraryFolderCommand { get; }

    public AsyncRelayCommand<GameCardViewModel?> PlayGameCommand { get; }
    public AsyncRelayCommand ApplyRecommendedSetupCommand { get; }
    public AsyncRelayCommand FindRequiredComponentsCommand { get; }
    public RelayCommand CancelSetupCommand { get; }
    public RelayCommand CloseSetupDrawerCommand { get; }
    public AsyncRelayCommand<ComponentRequirementItem?> LocateComponentCommand { get; }
    public RelayCommand<string?> OpenExternalUrlCommand { get; }
    public AsyncRelayCommand VerifyGameHealthCommand { get; }
    public AsyncRelayCommand UninstallGameIntegrationCommand { get; }

    public AsyncRelayCommand ChangeCoverArtworkCommand { get; }
    public AsyncRelayCommand ChangeHeroArtworkCommand { get; }
    public AsyncRelayCommand RefreshArtworkCommand { get; }
    public RelayCommand SaveSteamGridDbKeyCommand { get; }
    public RelayCommand ClearArtworkCacheCommand { get; }

    public ConcurrentAsyncRelayCommand<BackendComponentItem?> DownloadBackendCommand { get; }
    public RelayCommand<BackendComponentItem?> CancelBackendDownloadCommand { get; }
    public RelayCommand<UpdateCheckItem?> CancelUpdateItemCommand { get; }
    public RelayCommand<BackendComponentItem?> ToggleComponentDetailsCommand { get; }
    public RelayCommand<string?> SelectEnhancementCategoryCommand { get; }
    public AsyncRelayCommand<BackendComponentItem?> ConnectLocalBackendCommand { get; }
    public AsyncRelayCommand<BackendComponentItem?> ImportLocalBackendArchiveCommand { get; }
    public AsyncRelayCommand<BackendComponentItem?> RollbackBackendCommand { get; }
    public AsyncRelayCommand<BackendComponentItem?> VerifyBackendCommand { get; }
    public AsyncRelayCommand<BackendComponentItem?> RepairBackendCommand { get; }
    public AsyncRelayCommand<BackendComponentItem?> RemoveBackendCommand { get; }
    public RelayCommand OpenNeuralSetupCommand { get; }
    public AsyncRelayCommand ImportUserDllCommand { get; }
    public AsyncRelayCommand ImportNvidiaDllCommand { get; }
    public AsyncRelayCommand ScanGamesForNeuralDllCommand { get; }
    public AsyncRelayCommand ScanDownloadsForNeuralDllCommand { get; }
    public RelayCommand CancelNeuralScanCommand { get; }
    public RelayCommand OpenDllVaultCommand { get; }
    public AsyncRelayCommand VerifyImportedNeuralDllCommand { get; }
    public AsyncRelayCommand RemoveImportedNeuralDllCommand { get; }
    public AsyncRelayCommand<DiscoveredDllCandidate?> UseDiscoveredNeuralCandidateCommand { get; }
    public RelayCommand LaunchNeuralHubCommand { get; }
    public AsyncRelayCommand ScanGameLibrariesForDllsCommand { get; }
    public AsyncRelayCommand AutoDiscoverNowCommand { get; }
    public AsyncRelayCommand<string?> DeleteUserDllCommand { get; }

    public bool IsDownloadsWatcherActive { get => _isDownloadsWatcherActive; private set => SetProperty(ref _isDownloadsWatcherActive, value); }

    public AsyncRelayCommand CheckUpdatesNowCommand { get; }
    public ConcurrentAsyncRelayCommand<UpdateCheckItem?> ApplyUpdateItemCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ExportHardwareProfileCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public RelayCommand DismissAlertCommand { get; }

    public RelayCommand NextOnboardingCommand { get; }
    public RelayCommand SkipOnboardingCommand { get; }
    public RelayCommand OpenRecommendedGameFromOnboardingCommand { get; }
    public RelayCommand ShowOnboardingAgainCommand { get; }

    public MainViewModel(
        IGpuDetector gpuDetector,
        ICapabilityEngine capabilityEngine,
        ICompatibilityRepository repo,
        IGameLibraryScanner scanner,
        IArtworkDiscoveryService artworkService,
        IPackageManager packageManager,
        IUserDllVault dllVault,
        IBackendOrchestrator orchestrator,
        IIntelligentComponentResolver componentResolver,
        IGameInstaller installer,
        IGameLaunchService launchService,
        IUpdateCoordinator updateCoordinator,
        ILumaLogger logger,
        IUserDialogService dialogs,
        IWindowsStartupService? startupService = null,
        TrayLifecycleController? trayController = null)
    {
        _gpuDetector = gpuDetector;
        _capabilityEngine = capabilityEngine;
        _repo = repo;
        _scanner = scanner;
        _artworkService = artworkService;
        _packageManager = packageManager;
        _dllVault = dllVault;
        _orchestrator = orchestrator;
        _componentResolver = componentResolver;
        _installer = installer;
        _launchService = launchService;
        _updateCoordinator = updateCoordinator;
        _logger = logger;
        _dialogs = dialogs;
        _startupService = startupService ?? new InMemoryStartupServiceStub();
        _trayController = trayController ?? new TrayLifecycleController();

        NavigateCommand = new RelayCommand(p =>
        {
            if (p is LumaPage page) ActivePage = page;
            else if (p is string s)
            {
                if (s.Equals("AvailableEnhancements", StringComparison.OrdinalIgnoreCase)) ActivePage = LumaPage.EnhancementLibrary;
                else if (s.Equals("DownloadsAndUpdates", StringComparison.OrdinalIgnoreCase)) ActivePage = LumaPage.Downloads;
                else if (Enum.TryParse<LumaPage>(s, out var parsed)) ActivePage = parsed;
            }
        });

        BackToGamesCommand = new RelayCommand(_ => ActivePage = LumaPage.MyGames);

        OpenGameDetailsCommand = new RelayCommand<GameCardViewModel?>(card =>
        {
            if (card == null) return;
            SelectedGame = card;
            FeaturedGame = card;
            ActivePage = LumaPage.GameDetails;
            _ = RefreshSelectedGameComponentsAsync();
        });

        SelectFeaturedGameCommand = new RelayCommand<GameCardViewModel?>(card =>
        {
            if (card != null)
            {
                FeaturedGame = card;
                SelectedGame = card;
            }
        });

        SelectHomeFilterCommand = new RelayCommand<string?>(mode =>
        {
            if (!string.IsNullOrWhiteSpace(mode))
                HomeFilterMode = mode;
        });

        EnhanceGameFromHomeCommand = new AsyncRelayCommand<GameCardViewModel?>(async card =>
        {
            var target = card ?? FeaturedGame ?? SelectedGame;
            if (target == null) return;
            SelectedGame = target;
            FeaturedGame = target;
            await ExecuteGuidedSetupForSelectedAsync();
        });

        SelectLibraryTabCommand = new RelayCommand<string?>(tab =>
        {
            if (!string.IsNullOrWhiteSpace(tab)) SelectedLibraryTab = tab;
        });

        SelectPresetCommand = new RelayCommand<string?>(presetStr =>
        {
            if (SelectedGame == null || !Enum.TryParse<EnhancementProfilePreset>(presetStr, out var preset)) return;
            CapabilityEngine.ApplyPreset(SelectedGame.Profile, preset, SelectedGame.Game, GpuInfo, UserDlls.ToList());
            CapabilityEngine.UpdateNeuralActivationDiagnostics(SelectedGame.Game, SelectedGame.Profile, GpuInfo, UserDlls.ToList(), Backends.ToList());
            SelectedGame.NotifyStatusUpdated();
            OnPropertyChanged(nameof(SelectedGame));
            _ = PersistGameProfileAsync(SelectedGame.Profile);
            _ = RefreshSelectedGameComponentsAsync();
            ShowToast($"Selected {preset} profile for {SelectedGame.Title}.");
        });

        RescanLibrariesCommand = new AsyncRelayCommand(_ => ScanLibrariesAsync());
        CancelScanCommand = new RelayCommand(_ => _scanCts?.Cancel(), _ => IsScanning);
        AddGameManuallyCommand = new AsyncRelayCommand(_ => AddGameManuallyAsync());
        AddCustomLibraryFolderCommand = new AsyncRelayCommand(_ => AddLibraryFolderAsync());
        RemoveLibraryFolderCommand = new AsyncRelayCommand<string?>(p => RemoveLibraryFolderAsync(p));

        PlayGameCommand = new AsyncRelayCommand<GameCardViewModel?>(card => PlayGameAsync(card));
        ApplyRecommendedSetupCommand = new AsyncRelayCommand(_ => ExecuteGuidedSetupForSelectedAsync(), _ => SelectedGame != null);
        FindRequiredComponentsCommand = new AsyncRelayCommand(_ => RunAiVisualsSetupAsync(), _ => SelectedGame != null);
        CancelSetupCommand = new RelayCommand(_ => _setupCts?.Cancel(), _ => IsSetupRunning);
        CloseSetupDrawerCommand = new RelayCommand(_ => { if (!IsSetupRunning) IsSetupDrawerOpen = false; });
        LocateComponentCommand = new AsyncRelayCommand<ComponentRequirementItem?>(c => LocateComponentAsync(c));
        OpenExternalUrlCommand = new RelayCommand<string?>(url => { if (!string.IsNullOrWhiteSpace(url)) _dialogs.OpenUrl(url); });

        VerifyGameHealthCommand = new AsyncRelayCommand(_ => VerifySelectedGameHealthAsync(), _ => SelectedGame != null);
        UninstallGameIntegrationCommand = new AsyncRelayCommand(_ => UninstallSelectedGameAsync(), _ => SelectedGame != null);

        ChangeCoverArtworkCommand = new AsyncRelayCommand(_ => PickCustomArtworkAsync(isHero: false));
        ChangeHeroArtworkCommand = new AsyncRelayCommand(_ => PickCustomArtworkAsync(isHero: true));
        RefreshArtworkCommand = new AsyncRelayCommand(_ => RefreshAllArtworkAsync());
        SaveSteamGridDbKeyCommand = new RelayCommand(_ =>
        {
            _artworkService.SaveSteamGridDbApiKey(SteamGridDbKeyInput);
            Settings.SteamGridDbKeyConfigured = _artworkService.HasSteamGridDbApiKey();
            _ = _repo.SaveSettingsAsync(Settings);
            OnPropertyChanged(nameof(Settings));
            ShowToast(Settings.SteamGridDbKeyConfigured ? "SteamGridDB API key stored safely in Windows Credential protection." : "SteamGridDB API key cleared.");
        });
        ClearArtworkCacheCommand = new RelayCommand(_ =>
        {
            _artworkService.ClearCache();
            _ = RefreshAllArtworkAsync();
            ShowToast("Artwork cache refreshed.");
        });

        DownloadBackendCommand = new ConcurrentAsyncRelayCommand<BackendComponentItem?>(
            b => DownloadBackendAsync(b),
            b => b != null,
            ex => ActionAlertMessage = $"Download failed: {ex.Message}");
        CancelBackendDownloadCommand = new RelayCommand<BackendComponentItem?>(b => b?.CancelOperation());
        CancelUpdateItemCommand = new RelayCommand<UpdateCheckItem?>(u =>
        {
            if (u == null) return;
            ComponentItems.FirstOrDefault(c => c.Id.Equals(u.ComponentId, StringComparison.OrdinalIgnoreCase))?.CancelOperation();
        });
        ToggleComponentDetailsCommand = new RelayCommand<BackendComponentItem?>(b =>
        {
            if (b != null) b.IsDetailsExpanded = !b.IsDetailsExpanded;
        });
        SelectEnhancementCategoryCommand = new RelayCommand<string?>(cat =>
        {
            if (!string.IsNullOrWhiteSpace(cat)) SelectedEnhancementCategory = cat;
        });
        ConnectLocalBackendCommand = new AsyncRelayCommand<BackendComponentItem?>(b => ConnectLocalBackendAsync(b));
        ImportLocalBackendArchiveCommand = new AsyncRelayCommand<BackendComponentItem?>(b => ImportLocalBackendAsync(b));
        RollbackBackendCommand = new AsyncRelayCommand<BackendComponentItem?>(b => RollbackBackendAsync(b));
        VerifyBackendCommand = new AsyncRelayCommand<BackendComponentItem?>(b => VerifyBackendAsync(b));
        RepairBackendCommand = new AsyncRelayCommand<BackendComponentItem?>(b => RepairBackendAsync(b));
        RemoveBackendCommand = new AsyncRelayCommand<BackendComponentItem?>(b => RemoveBackendAsync(b));
        OpenNeuralSetupCommand = new RelayCommand(_ =>
        {
            SelectedEnhancementCategory = "AI Visual Enhancement";
            ActivePage = LumaPage.EnhancementLibrary;
        });
        ImportUserDllCommand = new AsyncRelayCommand(_ => ImportUserDllAsync());
        ImportNvidiaDllCommand = new AsyncRelayCommand(_ => ImportNvidiaDllAsync());
        ScanGamesForNeuralDllCommand = new AsyncRelayCommand(_ => ScanInstalledGamesForNeuralDllAsync());
        ScanDownloadsForNeuralDllCommand = new AsyncRelayCommand(_ => ScanDownloadsForNeuralDllAsync());
        CancelNeuralScanCommand = new RelayCommand(_ => _neuralScanCts?.Cancel(), _ => IsNeuralCandidateScanBusy);
        OpenDllVaultCommand = new RelayCommand(_ => OpenDllVaultFolder());
        VerifyImportedNeuralDllCommand = new AsyncRelayCommand(_ => VerifyImportedNeuralDllAsync());
        RemoveImportedNeuralDllCommand = new AsyncRelayCommand(_ => RemoveImportedNeuralDllAsync());
        UseDiscoveredNeuralCandidateCommand = new AsyncRelayCommand<DiscoveredDllCandidate?>(c => UseDiscoveredNeuralCandidateAsync(c));
        LaunchNeuralHubCommand = new RelayCommand(_ => LaunchOrOpenNeuralHub());
        ScanGameLibrariesForDllsCommand = new AsyncRelayCommand(_ => ScanGameLibrariesForDllsAsync());
        AutoDiscoverNowCommand = ScanGameLibrariesForDllsCommand;
        DeleteUserDllCommand = new AsyncRelayCommand<string?>(p => DeleteUserDllAsync(p));

        CheckUpdatesNowCommand = new AsyncRelayCommand(_ => _updateCoordinator.CheckNowAsync());
        ApplyUpdateItemCommand = new ConcurrentAsyncRelayCommand<UpdateCheckItem?>(
            item => ApplyUpdateItemAsync(item),
            item => item != null,
            ex => ActionAlertMessage = $"Component action failed: {ex.Message}");
        SaveSettingsCommand = new AsyncRelayCommand(_ => SaveSettingsAsync());
        ExportHardwareProfileCommand = new AsyncRelayCommand(_ => ExportHardwareProfileAsync());
        OpenLogsFolderCommand = new RelayCommand(_ => _dialogs.OpenFolder(_logger.LogDirectory));
        OpenBackupFolderCommand = new RelayCommand(_ => _dialogs.OpenFolder(_settings.BackupRootPath));
        DismissAlertCommand = new RelayCommand(_ => ActionAlertMessage = string.Empty);

        NextOnboardingCommand = new RelayCommand(_ =>
        {
            if (OnboardingStepIndex < 3) OnboardingStepIndex++;
            else CompleteOnboarding();
        });
        SkipOnboardingCommand = new RelayCommand(_ => CompleteOnboarding());
        OpenRecommendedGameFromOnboardingCommand = new RelayCommand(_ =>
        {
            CompleteOnboarding();
            var target = AllGames.FirstOrDefault(g => g.FriendlyStatus == FriendlyGameStatus.ReadyToEnhance) ?? AllGames.FirstOrDefault();
            if (target != null)
            {
                SelectedGame = target;
                FeaturedGame = target;
                ActivePage = LumaPage.GameDetails;
            }
        });
        ShowOnboardingAgainCommand = new RelayCommand(_ =>
        {
            OnboardingStepIndex = 1;
            IsOnboardingVisible = true;
        });

        _updateCoordinator.StatusChanged += status => RunOnUi(() => ApplyUpdateStatus(status));
        _logger.EntryLogged += entry => RunOnUi(() =>
        {
            RecentLogs.Insert(0, entry);
            while (RecentLogs.Count > 120) RecentLogs.RemoveAt(RecentLogs.Count - 1);
        });
    }

    public LumaPage ActivePage
    {
        get => _activePage;
        set
        {
            if (SetProperty(ref _activePage, value))
            {
                OnPropertyChanged(nameof(IsHomeActive));
                OnPropertyChanged(nameof(IsMyGamesActive));
                OnPropertyChanged(nameof(IsGameDetailsActive));
                OnPropertyChanged(nameof(IsEnhancementLibraryActive));
                OnPropertyChanged(nameof(IsEnhancementsActive));
                OnPropertyChanged(nameof(IsDownloadsActive));
                OnPropertyChanged(nameof(IsUpdatesActive));
                OnPropertyChanged(nameof(IsSettingsActive));
            }
        }
    }

    public bool IsHomeActive => ActivePage == LumaPage.Home;
    public bool IsMyGamesActive => ActivePage == LumaPage.MyGames;
    public bool IsGameDetailsActive => ActivePage == LumaPage.GameDetails;
    public bool IsEnhancementLibraryActive => ActivePage == LumaPage.EnhancementLibrary;
    public bool IsEnhancementsActive => IsEnhancementLibraryActive;
    public bool IsDownloadsActive => ActivePage == LumaPage.Downloads;
    public bool IsUpdatesActive => IsDownloadsActive;
    public bool IsSettingsActive => ActivePage == LumaPage.Settings;

    public string SelectedEnhancementCategory
    {
        get => _selectedEnhancementCategory;
        set
        {
            if (SetProperty(ref _selectedEnhancementCategory, value))
                ApplyComponentCategoryFilter();
        }
    }

    public string HomeFilterMode
    {
        get => _homeFilterMode;
        set
        {
            if (SetProperty(ref _homeFilterMode, value))
                RefreshHomeStrip();
        }
    }

    public DetectedGpuInfo GpuInfo { get => _gpuInfo; set => SetProperty(ref _gpuInfo, value); }
    public HardwareCapabilityProfile HardwareProfile
    {
        get => _hardwareProfile;
        set
        {
            if (SetProperty(ref _hardwareProfile, value))
            {
                OnPropertyChanged(nameof(NeuralCapability));
                OnPropertyChanged(nameof(SuperResCapability));
                OnPropertyChanged(nameof(FrameGenCapability));
                OnPropertyChanged(nameof(RayReconCapability));
            }
        }
    }

    public FeatureCapabilityEvaluation? NeuralCapability => HardwareProfile.Evaluations.GetValueOrDefault(RenderingFeatureType.NeuralRendering);
    public FeatureCapabilityEvaluation? SuperResCapability => HardwareProfile.Evaluations.GetValueOrDefault(RenderingFeatureType.SuperResolution);
    public FeatureCapabilityEvaluation? FrameGenCapability => HardwareProfile.Evaluations.GetValueOrDefault(RenderingFeatureType.FrameGeneration);
    public FeatureCapabilityEvaluation? RayReconCapability => HardwareProfile.Evaluations.GetValueOrDefault(RenderingFeatureType.RayReconstruction);

    public AppSettingsModel Settings
    {
        get => _settings;
        set { if (SetProperty(ref _settings, value)) OnPropertyChanged(nameof(UseExperimentalChannel)); }
    }

    public bool UseExperimentalChannel
    {
        get => Settings.UpdateChannel == UpdateChannel.Experimental;
        set
        {
            var next = value ? UpdateChannel.Experimental : UpdateChannel.Stable;
            if (Settings.UpdateChannel != next)
            {
                Settings.UpdateChannel = next;
                OnPropertyChanged();
                ReevaluateAllCapabilities();
            }
        }
    }

    public UpdateCoordinatorStatus UpdateStatus { get => _updateStatus; set => SetProperty(ref _updateStatus, value); }

    public GameCardViewModel? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (SetProperty(ref _selectedGame, value))
            {
                ApplyRecommendedSetupCommand.RaiseCanExecuteChanged();
                VerifyGameHealthCommand.RaiseCanExecuteChanged();
                UninstallGameIntegrationCommand.RaiseCanExecuteChanged();
                RefreshSelectedGameContext();
                _ = RefreshSelectedGameComponentsAsync();
                _ = LoadSelectedGameBackupsAsync();
            }
        }
    }

    public GameCardViewModel? FeaturedGame { get => _featuredGame; set => SetProperty(ref _featuredGame, value); }
    public ComponentResolutionPlan? SelectedGameComponents { get => _selectedGameComponents; set => SetProperty(ref _selectedGameComponents, value); }

    public string SearchQuery { get => _searchQuery; set { if (SetProperty(ref _searchQuery, value)) ApplyFilters(); } }
    public string SelectedLibraryTab { get => _selectedLibraryTab; set { if (SetProperty(ref _selectedLibraryTab, value)) ApplyFilters(); } }
    public string SelectedLauncherFilter { get => _selectedLauncherFilter; set { if (SetProperty(ref _selectedLauncherFilter, value)) ApplyFilters(); } }
    public string SelectedSortOrder { get => _selectedSortOrder; set { if (SetProperty(ref _selectedSortOrder, value)) ApplyFilters(); } }

    public bool IsScanning { get => _isScanning; set { if (SetProperty(ref _isScanning, value)) CancelScanCommand.RaiseCanExecuteChanged(); } }
    public int ScanProgressPercent { get => _scanProgressPercent; set => SetProperty(ref _scanProgressPercent, value); }
    public string ScanStatusMessage { get => _scanStatusMessage; set => SetProperty(ref _scanStatusMessage, value); }

    public string ToastMessage { get => _toastMessage; set => SetProperty(ref _toastMessage, value); }
    public bool IsToastVisible { get => _isToastVisible; set => SetProperty(ref _isToastVisible, value); }
    public string ActionAlertMessage { get => _actionAlertMessage; set => SetProperty(ref _actionAlertMessage, value); }

    public bool IsSetupDrawerOpen { get => _isSetupDrawerOpen; set => SetProperty(ref _isSetupDrawerOpen, value); }
    public bool IsSetupRunning { get => _isSetupRunning; set { if (SetProperty(ref _isSetupRunning, value)) CancelSetupCommand.RaiseCanExecuteChanged(); } }
    public bool SetupSucceeded { get => _setupSucceeded; set => SetProperty(ref _setupSucceeded, value); }
    public string SetupErrorMessage { get => _setupErrorMessage; set => SetProperty(ref _setupErrorMessage, value); }
    public string SetupDrawerHeader { get => _setupDrawerHeader; set => SetProperty(ref _setupDrawerHeader, value); }
    public bool IsAiVisualsSetupDrawer { get => _isAiVisualsSetupDrawer; set => SetProperty(ref _isAiVisualsSetupDrawer, value); }
    public string WindowsStartupStatusText { get => _windowsStartupStatusText; set => SetProperty(ref _windowsStartupStatusText, value); }

    public IReadOnlyList<NeuralRenderingModelType> AvailableNeuralModels { get; } = new[] { NeuralRenderingModelType.CC, NeuralRenderingModelType.HT };
    public NeuralRenderingModelType SelectedNeuralImportModel
    {
        get => _selectedNeuralImportModel;
        set
        {
            if (SetProperty(ref _selectedNeuralImportModel, value))
            {
                OnPropertyChanged(nameof(NeuralRequiredModelDisplay));
                _ = RefreshNeuralDllSectionAsync();
            }
        }
    }
    public string NeuralRequiredDllName => "nvngx_dlssnr.dll";
    public string NeuralRequiredModelDisplay => SelectedNeuralImportModel == NeuralRenderingModelType.CC
        ? "CC Model (Fastest, v0.9.0)"
        : "HT Model (Standard)";
    public string NeuralMinimumVersionRequired { get => _neuralMinimumVersionRequired; set => SetProperty(ref _neuralMinimumVersionRequired, value); }
    public string NeuralDetectedVersion { get => _neuralDetectedVersion; set => SetProperty(ref _neuralDetectedVersion, value); }
    public string NeuralImportStatusText { get => _neuralImportStatusText; set => SetProperty(ref _neuralImportStatusText, value); }
    public string NeuralValidationStatusText { get => _neuralValidationStatusText; set => SetProperty(ref _neuralValidationStatusText, value); }
    public string NeuralValidationDetailText { get => _neuralValidationDetailText; set => SetProperty(ref _neuralValidationDetailText, value); }
    public string NeuralLocalInstallationStatusText { get => _neuralLocalInstallationStatusText; set => SetProperty(ref _neuralLocalInstallationStatusText, value); }
    public bool HasImportedNeuralDll { get => _hasImportedNeuralDll; set => SetProperty(ref _hasImportedNeuralDll, value); }
    public bool IsNeuralHubDetected { get => _isNeuralHubDetected; set => SetProperty(ref _isNeuralHubDetected, value); }
    public string NeuralHubExecutablePath { get => _neuralHubExecutablePath; set => SetProperty(ref _neuralHubExecutablePath, value); }
    public bool IsNeuralCandidateScanBusy { get => _isNeuralCandidateScanBusy; set { if (SetProperty(ref _isNeuralCandidateScanBusy, value)) CancelNeuralScanCommand.RaiseCanExecuteChanged(); } }
    public string NeuralCandidateScanSummary { get => _neuralCandidateScanSummary; set => SetProperty(ref _neuralCandidateScanSummary, value); }

    public event Action? TrayPreferenceSyncRequested;

    public bool IsOnboardingVisible { get => _isOnboardingVisible; set => SetProperty(ref _isOnboardingVisible, value); }
    public int OnboardingStepIndex
    {
        get => _onboardingStepIndex;
        set
        {
            if (SetProperty(ref _onboardingStepIndex, value))
            {
                OnPropertyChanged(nameof(OnboardingTitle));
                OnPropertyChanged(nameof(OnboardingDescription));
                OnPropertyChanged(nameof(OnboardingPrimaryButtonText));
                OnPropertyChanged(nameof(IsOnboardingStep2));
                OnPropertyChanged(nameof(IsOnboardingStep3));
            }
        }
    }

    public bool IsOnboardingStep2 => OnboardingStepIndex == 2;
    public bool IsOnboardingStep3 => OnboardingStepIndex >= 3;

    public string OnboardingTitle => OnboardingStepIndex switch
    {
        1 => "Meet Project Luma",
        2 => "Find Your Games",
        _ => "Ready to Play"
    };

    public string OnboardingDescription => OnboardingStepIndex switch
    {
        1 => "Your AMD Gaming Enhancement Companion. Project Luma finds your installed PC games, checks what graphics enhancements your PC supports, and configures everything in one click.",
        2 => $"Scanned installed launchers (Steam, Epic, GOG, Ubisoft, EA, Battle.net, and Xbox) and found {AllGames.Count} game(s). Add extra folders anytime below or in Settings.",
        _ => $"Graphics card: {GpuInfo.ModelName}\nInstalled games found: {AllGames.Count}\nReady for enhancements: {ReadyGamesCount}\nRecommended first game: {FeaturedGame?.Title ?? AllGames.FirstOrDefault()?.Title ?? "Add a game to get started"}"
    };

    public string OnboardingPrimaryButtonText => OnboardingStepIndex < 3 ? "Continue →" : "Ready to Play";
    public string SteamGridDbKeyInput { get => _steamGridDbKeyInput; set => SetProperty(ref _steamGridDbKeyInput, value); }

    public int ReadyGamesCount => AllGames.Count(g => g.FriendlyStatus is FriendlyGameStatus.ReadyToEnhance or FriendlyGameStatus.Enhanced);
    public int EnhancedGamesCount => AllGames.Count(g => g.FriendlyStatus == FriendlyGameStatus.Enhanced);
    public int PendingUpdatesCount => UpdateItems.Count(i => i.CanApply);
    public string SidebarGpuSummary => $"{GpuInfo.ModelName.Replace("AMD ", "")} · {ReadyGamesCount} ready";

    public IReadOnlyList<NeuralQualityMode> AvailableNeuralModes => _orchestrator.GetSupportedNeuralModes(GpuInfo.Architecture);
    public IReadOnlyList<string> AvailableUpscalerEngines => _orchestrator.GetSupportedUpscalerEngines(GpuInfo.Architecture);
    public IReadOnlyList<UpscalingQualityPreset> AvailableUpscalingPresets =>
        _orchestrator.GetSupportedPresets(
            SelectedGame?.Profile.SelectedUpscalerBackend ?? "optiscaler",
            SelectedGame?.Profile.SelectedUpscalerEngine ?? "FSR 4.1.2 INT8");
    public IReadOnlyList<string> AvailableFrameGenEngines =>
        _orchestrator.GetSupportedFrameGenEngines(GpuInfo.Architecture, SelectedGame?.Game.GraphicsApi ?? GraphicsApi.DirectX12)
            .Select(e => e.State == CapabilityState.Unsupported ? $"{e.Name} [Unsupported]" : e.Name)
            .ToList();
    public IReadOnlyList<string> AvailableProxyDllNames { get; } = new[] { "dxgi.dll", "version.dll", "winmm.dll" };

    public async Task InitializeAsync(bool autoScan = true, bool showOnboardingIfFirstRun = true)
    {
        await _repo.InitializeAsync();
        Settings = await _repo.LoadSettingsAsync();
        Settings.SteamGridDbKeyConfigured = _artworkService.HasSteamGridDbApiKey();

        // Sync LaunchOnWindowsStartup directly from real Windows Registry state rather than trusting SQLite alone
        if (_startupService is not InMemoryStartupServiceStub)
        {
            bool actualStartup = _startupService.IsEnabled();
            if (Settings.LaunchOnWindowsStartup != actualStartup)
            {
                Settings.LaunchOnWindowsStartup = actualStartup;
                await _repo.SaveSettingsAsync(Settings);
            }
            UpdateWindowsStartupStatusDisplay(actualStartup);
        }
        else
        {
            UpdateWindowsStartupStatusDisplay(Settings.LaunchOnWindowsStartup);
        }

        _trayController.SyncPreference(Settings.MinimizeToTray);
        TrayPreferenceSyncRequested?.Invoke();

        CustomLibraryPaths.Clear();
        foreach (string p in Settings.CustomLibraryPaths) CustomLibraryPaths.Add(p);
        foreach (var entry in _logger.RecentEntries.Reverse().Take(120)) RecentLogs.Add(entry);

        GpuInfo = await _gpuDetector.DetectPrimaryGpuAsync();
        OnPropertyChanged(nameof(SidebarGpuSummary));
        OnPropertyChanged(nameof(AvailableNeuralModes));
        OnPropertyChanged(nameof(AvailableUpscalerEngines));

        await ReloadCatalogsAsync();

        var cachedGames = await _repo.LoadDiscoveredGamesAsync();
        if (cachedGames.Count > 0)
            await PopulateGamesAsync(cachedGames);

        if (autoScan)
            await ScanLibrariesAsync();

        if (showOnboardingIfFirstRun && !Settings.HasCompletedOnboarding)
        {
            OnboardingStepIndex = 1;
            IsOnboardingVisible = true;
        }
    }

    public async Task ReloadCatalogsAsync()
    {
        var manifests = await _packageManager.GetManifestsAsync();
        Backends.Clear();
        foreach (var m in manifests)
            Backends.Add(m);

        var byId = manifests.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        if (ComponentItems.Count == 0)
        {
            foreach (var m in manifests)
            {
                BackendManifest? parent = !string.IsNullOrWhiteSpace(m.ParentComponentId)
                    ? byId.GetValueOrDefault(m.ParentComponentId)
                    : null;
                var row = new BackendComponentItem(m, GpuInfo, parent);
                await row.RefreshStateFromManifestAsync(GpuInfo, parent);
                ComponentItems.Add(row);
            }
        }
        else
        {
            foreach (var existing in ComponentItems)
            {
                if (byId.TryGetValue(existing.Id, out var updated))
                {
                    existing.Manifest.CurrentInstalledVersion = updated.CurrentInstalledVersion;
                    existing.Manifest.LatestDiscoveredVersion = updated.LatestDiscoveredVersion;
                    existing.Manifest.StagedInstallPath = updated.StagedInstallPath;
                    existing.Manifest.ExpectedSha256 = updated.ExpectedSha256;
                    BackendManifest? parent = !string.IsNullOrWhiteSpace(existing.Manifest.ParentComponentId)
                        ? byId.GetValueOrDefault(existing.Manifest.ParentComponentId)
                        : null;
                    if (!existing.IsBusy)
                        await existing.RefreshStateFromManifestAsync(GpuInfo, parent);
                }
            }
        }

        ApplyComponentCategoryFilter();

        var dlls = await _dllVault.GetImportedDllsAsync();
        UserDlls.Clear();
        foreach (var d in dlls) UserDlls.Add(d);

        var history = await _repo.GetUpdateHistoryAsync(40);
        UpdateHistoryRows.Clear();
        foreach (string row in history) UpdateHistoryRows.Add(row);

        await RefreshNeuralDllSectionAsync();
        ReevaluateAllCapabilities();
        await _repo.SaveHardwareProfileAsync(HardwareProfile);
    }

    private void ApplyComponentCategoryFilter()
    {
        FilteredComponentItems.Clear();
        foreach (var item in ComponentItems)
        {
            if (SelectedEnhancementCategory.Equals("All Categories", StringComparison.OrdinalIgnoreCase) ||
                item.CategoryDisplayName.Equals(SelectedEnhancementCategory, StringComparison.OrdinalIgnoreCase))
            {
                FilteredComponentItems.Add(item);
            }
        }
    }

    public void ReevaluateAllCapabilities()
    {
        HardwareProfile = _capabilityEngine.EvaluateHardware(GpuInfo, UserDlls.ToList(), Backends.ToList(), Settings.UpdateChannel);
        var dlls = UserDlls.ToList();
        var manifests = Backends.ToList();
        var capEngine = _capabilityEngine as CapabilityEngine;

        foreach (var card in AllGames)
        {
            var seed = _repo.FindSeedForGame(card.Game);
            if (capEngine != null)
            {
                capEngine.ReevaluatePreservingUserSettings(card.Profile, card.Game, GpuInfo, dlls, manifests, seed, Settings.UpdateChannel);
            }
            else
            {
                var updated = _capabilityEngine.EvaluateGame(card.Game, GpuInfo, dlls, manifests, seed, Settings.UpdateChannel);
                updated.SelectedPreset = card.Profile.SelectedPreset;
                updated.SelectedUpscalerBackend = card.Profile.SelectedUpscalerBackend;
                updated.SelectedUpscalerEngine = card.Profile.SelectedUpscalerEngine;
                updated.SelectedUpscalingPreset = card.Profile.SelectedUpscalingPreset;
                updated.Sharpness = card.Profile.Sharpness;
                updated.ShowFsr4Watermark = card.Profile.ShowFsr4Watermark;
                updated.EnableSuperResolution = card.Profile.EnableSuperResolution;
                updated.EnableFrameGeneration = card.Profile.EnableFrameGeneration;
                updated.SelectedFrameGenEngine = card.Profile.SelectedFrameGenEngine;
                updated.EnableHudFix = card.Profile.EnableHudFix;
                updated.EnableAntiLag2 = card.Profile.EnableAntiLag2;
                updated.EnableNeuralRendering = card.Profile.EnableNeuralRendering;
                updated.SelectedNeuralQuality = card.Profile.SelectedNeuralQuality;
                updated.SelectedNeuralModel = card.Profile.SelectedNeuralModel;
                updated.ProxyDllName = card.Profile.ProxyDllName;
                updated.OfflineAntiCheatConfirmed = card.Profile.OfflineAntiCheatConfirmed;
                card.Profile = updated;
                CapabilityEngine.PopulateFriendlyStatuses(card.Game, card.Profile);
                CapabilityEngine.UpdateNeuralActivationDiagnostics(card.Game, card.Profile, GpuInfo, dlls, manifests);
            }
            card.NotifyStatusUpdated();
        }
        ApplyFilters();
    }

    public async Task ScanLibrariesAsync()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        ScanProgressPercent = 0;
        ScanStatusMessage = "Scanning your installed games...";

        try
        {
            var progress = new Progress<GameLibraryScanProgress>(p =>
            {
                ScanProgressPercent = p.PercentComplete;
                ScanStatusMessage = p.StatusMessage;
            });

            var found = await _scanner.ScanAllAsync(Settings.CustomLibraryPaths, progress, _scanCts.Token);
            await PopulateGamesAsync(found);
            await _repo.SaveDiscoveredGamesAsync(found);
            ScanStatusMessage = $"Library ready ({found.Count} games)";
            ShowToast($"Library scan complete — {found.Count} game(s) ready.");
        }
        catch (OperationCanceledException)
        {
            ScanStatusMessage = "Library scan cancelled";
        }
        catch (Exception ex)
        {
            _logger.Error("UI", "Game library scan failed", ex);
            ActionAlertMessage = $"Could not finish scanning game libraries: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            ScanProgressPercent = 100;
        }
    }

    public async Task PopulateGamesAsync(IReadOnlyList<DiscoveredGame> games, bool resolveArtwork = true)
    {
        string? prevSelectedId = SelectedGame?.Id;
        string? prevSelectedTitle = SelectedGame?.Title;
        string? prevFeaturedId = FeaturedGame?.Id;

        AllGames.Clear();
        var manifests = Backends.ToList();
        var dlls = UserDlls.ToList();

        foreach (var g in games)
        {
            var seed = _repo.FindSeedForGame(g);
            if (seed != null && seed.TestedOnRx6600)
                g.CompatibilityTier = GameCompatibilityTier.Tested;
            else if (g.AntiCheatStatus != AntiCheatStatus.None || !g.Is64Bit)
                g.CompatibilityTier = GameCompatibilityTier.Unsupported;
            else
                g.CompatibilityTier = GameCompatibilityTier.CompatibleUntested;

            var savedProfile = await _repo.LoadGameProfileAsync(g.Id);
            var evaluated = _capabilityEngine.EvaluateGame(g, GpuInfo, dlls, manifests, seed, Settings.UpdateChannel);

            if (savedProfile != null)
            {
                evaluated.SelectedPreset = savedProfile.SelectedPreset;
                evaluated.PresetSummary = string.IsNullOrWhiteSpace(savedProfile.PresetSummary)
                    ? (savedProfile.SelectedPreset == EnhancementProfilePreset.Custom
                        ? "Customize individual supported visual enhancements and fine-tune quality settings."
                        : evaluated.PresetSummary)
                    : savedProfile.PresetSummary;
                evaluated.EnableNeuralRendering = savedProfile.EnableNeuralRendering;
                evaluated.SelectedNeuralQuality = savedProfile.SelectedNeuralQuality;
                evaluated.SelectedNeuralModel = savedProfile.SelectedNeuralModel;
                evaluated.EnableNeuralMultipass = savedProfile.EnableNeuralMultipass;
                evaluated.EnableSuperResolution = savedProfile.EnableSuperResolution;
                if (!string.IsNullOrWhiteSpace(savedProfile.SelectedUpscalerBackend))
                    evaluated.SelectedUpscalerBackend = savedProfile.SelectedUpscalerBackend;
                if (!string.IsNullOrWhiteSpace(savedProfile.SelectedUpscalerEngine))
                    evaluated.SelectedUpscalerEngine = savedProfile.SelectedUpscalerEngine;
                evaluated.SelectedUpscalingPreset = savedProfile.SelectedUpscalingPreset;
                evaluated.Sharpness = savedProfile.Sharpness;
                evaluated.ShowFsr4Watermark = savedProfile.ShowFsr4Watermark;
                evaluated.EnableFrameGeneration = savedProfile.EnableFrameGeneration;
                if (!string.IsNullOrWhiteSpace(savedProfile.SelectedFrameGenEngine))
                    evaluated.SelectedFrameGenEngine = savedProfile.SelectedFrameGenEngine;
                evaluated.EnableHudFix = savedProfile.EnableHudFix;
                evaluated.EnableAntiLag2 = savedProfile.EnableAntiLag2;
                evaluated.EnableRayReconstruction = savedProfile.EnableRayReconstruction;
                if (!string.IsNullOrWhiteSpace(savedProfile.ProxyDllName))
                    evaluated.ProxyDllName = savedProfile.ProxyDllName;
                evaluated.OfflineAntiCheatConfirmed = savedProfile.OfflineAntiCheatConfirmed;
                if (_capabilityEngine is CapabilityEngine ce)
                    ce.ReevaluatePreservingUserSettings(evaluated, g, GpuInfo, dlls, manifests, seed, Settings.UpdateChannel);
                else
                {
                    CapabilityEngine.PopulateFriendlyStatuses(g, evaluated);
                    CapabilityEngine.UpdateNeuralActivationDiagnostics(g, evaluated, GpuInfo, dlls, manifests);
                }
            }

            if (!string.IsNullOrWhiteSpace(g.InstalledBackendId))
                g.HealthState = await _installer.VerifyInstallationHealthAsync(g);

            if (resolveArtwork)
                await _artworkService.ResolveArtworkAsync(g, Settings.EnableRemoteArtworkLookup);

            AllGames.Add(new GameCardViewModel(g, evaluated, OnGameCardProfileEdited));
        }

        SelectedGame = AllGames.FirstOrDefault(c => c.Id == prevSelectedId || c.Title == prevSelectedTitle)
                       ?? AllGames.FirstOrDefault(c => c.Title.Equals("Aniimo", StringComparison.OrdinalIgnoreCase))
                       ?? AllGames.FirstOrDefault();
        FeaturedGame = AllGames.FirstOrDefault(c => c.Id == prevFeaturedId)
                       ?? AllGames.FirstOrDefault(c => c.Title.Equals("Aniimo", StringComparison.OrdinalIgnoreCase))
                       ?? AllGames.FirstOrDefault(c => c.FriendlyStatus == FriendlyGameStatus.ReadyToEnhance)
                       ?? AllGames.FirstOrDefault();

        RefreshHomeStrip();
        OnPropertyChanged(nameof(ReadyGamesCount));
        OnPropertyChanged(nameof(EnhancedGamesCount));
        OnPropertyChanged(nameof(SidebarGpuSummary));
        ApplyFilters();
    }

    public void RefreshHomeStrip()
    {
        IEnumerable<GameCardViewModel> pool = HomeFilterMode switch
        {
            "Ready" => AllGames.Where(g => g.FriendlyStatus == FriendlyGameStatus.ReadyToEnhance),
            "Enhanced" => AllGames.Where(g => g.FriendlyStatus == FriendlyGameStatus.Enhanced),
            _ => AllGames
        };

        var ordered = new List<GameCardViewModel>();
        string[] priorityTitles = { "Aniimo", "Cyberpunk 2077", "Red Dead Redemption 2", "Alan Wake 2" };
        foreach (string title in priorityTitles)
        {
            var match = pool.FirstOrDefault(g => g.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
            if (match != null && !ordered.Contains(match))
                ordered.Add(match);
        }

        foreach (var item in pool)
        {
            if (ordered.Count >= 4) break;
            if (!ordered.Contains(item))
                ordered.Add(item);
        }

        if (ordered.Count == 0)
        {
            foreach (var item in AllGames.Take(4))
                ordered.Add(item);
        }

        HomeStripGames.Clear();
        foreach (var item in ordered.Take(4))
            HomeStripGames.Add(item);
    }

    public void ApplyFilters()
    {
        IEnumerable<GameCardViewModel> query = AllGames;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
            query = query.Where(g => g.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        query = SelectedLibraryTab switch
        {
            "Ready to Enhance" => query.Where(g => g.FriendlyStatus == FriendlyGameStatus.ReadyToEnhance),
            "Enhanced" => query.Where(g => g.FriendlyStatus == FriendlyGameStatus.Enhanced),
            "Setup Required" => query.Where(g => g.FriendlyStatus == FriendlyGameStatus.SetupRequired || g.FriendlyStatus == FriendlyGameStatus.LimitedSupport),
            "Unsupported" or "Not Supported" => query.Where(g => g.FriendlyStatus == FriendlyGameStatus.NotSupported),
            _ => query
        };

        if (SelectedLauncherFilter != "All Platforms")
            query = query.Where(g => g.LauncherBadge.Equals(SelectedLauncherFilter, StringComparison.OrdinalIgnoreCase));

        query = SelectedSortOrder switch
        {
            "Enhanced First" => query.OrderBy(g => g.FriendlyStatus).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            "Platform" => query.OrderBy(g => g.LauncherBadge).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
        };

        FilteredGames.Clear();
        foreach (var item in query) FilteredGames.Add(item);

        RebuildVirtualRows();
    }

    public void UpdateGridViewportWidth(double availableWidth)
    {
        if (availableWidth <= 0) return;
        double cappedWidth = Math.Min(availableWidth, 1440 - 64);
        // Card width = 196, gap = 16 -> 212px stride; subtract ~12px scrollbar reserve
        int cols = Math.Max(2, (int)Math.Floor((cappedWidth - 12 + 16) / 212.0));
        if (cols != _gridColumns)
        {
            _gridColumns = cols;
            RebuildVirtualRows();
        }
    }

    private void RebuildVirtualRows()
    {
        VirtualGameRows.Clear();
        int columns = Math.Max(1, _gridColumns);
        for (int i = 0; i < FilteredGames.Count; i += columns)
        {
            var slice = new List<GameCardViewModel>(columns);
            for (int j = i; j < Math.Min(i + columns, FilteredGames.Count); j++)
                slice.Add(FilteredGames[j]);
            VirtualGameRows.Add(new GameCoverRow(slice));
        }
    }

    private async Task ExecuteGuidedSetupForSelectedAsync()
    {
        if (SelectedGame == null || IsSetupRunning) return;

        _setupCts?.Cancel();
        _setupCts = new CancellationTokenSource();
        IsAiVisualsSetupDrawer = false;
        SetupDrawerHeader = "Setting up game enhancements";
        DiscoveredSetupCandidates.Clear();
        IsSetupDrawerOpen = true;
        IsSetupRunning = true;
        SetupSucceeded = false;
        SetupErrorMessage = string.Empty;

        lock (_setupStepsLock)
        {
            SetupSteps.Clear();
            string[] initialLabels =
            {
                "Checking graphics compatibility",
                "Selecting recommended enhancements",
                "Preparing required components",
                "Downloading enhancement files",
                "Preparing game configuration",
                "Creating a recovery point"
            };
            foreach (string lbl in initialLabels)
                SetupSteps.Add(new SetupProgressStep { Label = lbl, State = SetupStepState.Pending });
        }

        var progress = new OrderedUiProgress<SetupProgressStep>(ApplySetupStepUpdate);

        try
        {
            bool ok = await _componentResolver.ExecuteGuidedSetupAsync(SelectedGame.Game, SelectedGame.Profile, GpuInfo, progress, _setupCts.Token);
            if (ok)
            {
                SetupSucceeded = true;
                await ReloadCatalogsAsync();
                await _repo.SaveDiscoveredGamesAsync(AllGames.Select(c => c.Game));
                await LoadSelectedGameBackupsAsync();
                SelectedGame.NotifyStatusUpdated();
                OnPropertyChanged(nameof(ReadyGamesCount));
                OnPropertyChanged(nameof(EnhancedGamesCount));
                ShowToast($"Enhancements applied to {SelectedGame.Title}. Launch the game to enjoy!");
            }
            else
            {
                SetupErrorMessage = "One component needs your attention before enabling AI Visual Enhancement.";
            }
        }
        catch (OperationCanceledException)
        {
            SetupErrorMessage = "Setup was cancelled. No game files were modified.";
        }
        catch (Exception ex)
        {
            SetupErrorMessage = ex.Message;
            ActionAlertMessage = $"Setup could not complete for {SelectedGame.Title}: {ex.Message}";
        }
        finally
        {
            IsSetupRunning = false;
            await RefreshSelectedGameComponentsAsync();
        }
    }

    public async Task<AiVisualsSetupResult?> RunAiVisualsSetupAsync()
    {
        if (SelectedGame == null || IsSetupRunning) return null;

        _setupCts?.Cancel();
        _setupCts = new CancellationTokenSource();
        IsAiVisualsSetupDrawer = true;
        SetupDrawerHeader = "SETTING UP AI VISUAL ENHANCEMENT";
        DiscoveredSetupCandidates.Clear();
        IsSetupDrawerOpen = true;
        IsSetupRunning = true;
        SetupSucceeded = false;
        SetupErrorMessage = string.Empty;

        lock (_setupStepsLock)
        {
            SetupSteps.Clear();
            string[] stageLabels =
            {
                "Checking GPU compatibility",
                "Checking game compatibility",
                "Checking Neural Rendering backend",
                "Scanning game folders",
                "Scanning Downloads",
                "Validating model DLL",
                "Checking runtime initialization"
            };
            foreach (string lbl in stageLabels)
                SetupSteps.Add(new SetupProgressStep { Label = lbl, State = SetupStepState.Pending });
        }

        var progress = new OrderedUiProgress<SetupProgressStep>(ApplySetupStepUpdate);

        try
        {
            var dirs = AllGames.Select(g => g.Game.InstallDirectory)
                .Concat(Settings.CustomLibraryPaths)
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = await _componentResolver.ExecuteAiVisualsDiscoveryAsync(
                SelectedGame.Game,
                SelectedGame.Profile,
                GpuInfo,
                dirs,
                progress,
                _setupCts.Token);

            foreach (var cand in result.DiscoveredCandidates)
                DiscoveredSetupCandidates.Add(cand);

            await ReloadCatalogsAsync();
            await RefreshSelectedGameComponentsAsync();

            if (result.IsReadyToEnable)
            {
                SetupSucceeded = true;
                ShowToast($"AI Visual Enhancement prerequisites verified for {SelectedGame.Title}.");
            }
            else
            {
                SetupErrorMessage = result.BlockerSummary;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            SetupErrorMessage = "AI Visuals component discovery was cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            SetupErrorMessage = ex.Message;
            _logger.Error("UI", $"AI Visuals discovery failed for {SelectedGame.Title}", ex);
            ActionAlertMessage = $"AI Visuals discovery failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsSetupRunning = false;
        }
    }

    private void ApplySetupStepUpdate(SetupProgressStep step)
    {
        if (_disposed) return;
        RunOnUi(() =>
        {
            if (_disposed) return;
            lock (_setupStepsLock)
            {
                var existing = SetupSteps.FirstOrDefault(s => s.Label.Equals(step.Label, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    int idx = SetupSteps.IndexOf(existing);
                    SetupSteps[idx] = step;
                }
            }
        });
    }

    private sealed class OrderedUiProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private async void OnGameCardProfileEdited(GameCardViewModel card)
    {
        if (card == SelectedGame)
            OnPropertyChanged(nameof(SelectedGame));
        await PersistGameProfileAsync(card.Profile);
        if (card == SelectedGame)
            await RefreshSelectedGameComponentsAsync();
    }

    private async Task PersistGameProfileAsync(GameEnhancementProfile profile)
    {
        await _profileSaveLock.WaitAsync();
        try
        {
            await _repo.SaveGameProfileAsync(profile);
        }
        catch (Exception ex)
        {
            _logger.Error("Compatibility", $"Failed saving profile for {profile.GameTitle}", ex);
        }
        finally
        {
            _profileSaveLock.Release();
        }
    }

    private async Task RefreshSelectedGameComponentsAsync()
    {
        if (SelectedGame == null) { SelectedGameComponents = null; return; }
        SelectedGameComponents = await _componentResolver.AnalyzeRequirementsAsync(SelectedGame.Game, SelectedGame.Profile, GpuInfo);
    }

    private async Task LocateComponentAsync(ComponentRequirementItem? item)
    {
        if (item == null) return;
        if (item.ComponentId is "amd-hip-72" or "dlss-nr-on-amd")
        {
            _dialogs.OpenUrl(item.OfficialGuidanceUrl);
            StartDownloadsFolderWatcher();
            ShowToast("Opened official download source. Project Luma is watching your Downloads folder.");
            return;
        }

        // First try automatic discovery across game libraries + Downloads before opening file picker
        var dirs = AllGames.Select(g => g.Game.InstallDirectory)
            .Concat(Settings.CustomLibraryPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        int autoCount = await _componentResolver.AutoDiscoverDownloadedComponentsAsync(additionalGameDirs: dirs);
        if (autoCount > 0)
        {
            await ReloadCatalogsAsync();
            await RefreshSelectedGameComponentsAsync();
            if (SelectedGameComponents != null && !SelectedGameComponents.RequiresUserAttention)
            {
                ShowToast("Detected & verified required component automatically on your PC.");
                return;
            }
        }

        string? file = _dialogs.PickFile("Locate Required Component File", "Component Files (*.dll)|*.dll");
        if (string.IsNullOrWhiteSpace(file)) return;

        try
        {
            var rec = await _componentResolver.LocateComponentOnPcAsync(file);
            await ReloadCatalogsAsync();
            await RefreshSelectedGameComponentsAsync();
            ShowToast($"Verified {rec.FileName} (v{rec.FileVersion}).");
        }
        catch (Exception ex)
        {
            ActionAlertMessage = $"Could not use selected file: {ex.Message}";
        }
    }

    private async Task AddGameManuallyAsync()
    {
        string? file = _dialogs.PickFile("Select Game Executable (.exe)", "Game Executables (*.exe)|*.exe");
        if (string.IsNullOrWhiteSpace(file)) return;

        var discovered = await _scanner.InspectSingleDirectoryOrExeAsync(file, GameLauncherType.Manual);
        if (discovered == null)
        {
            ActionAlertMessage = "Selected file is not a valid Windows game executable.";
            return;
        }

        await _artworkService.ResolveArtworkAsync(discovered, Settings.EnableRemoteArtworkLookup);
        var list = AllGames.Select(c => c.Game).ToList();
        if (!list.Any(g => g.ExecutablePath.Equals(discovered.ExecutablePath, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(discovered);
            await _repo.SaveDiscoveredGamesAsync(list);
            await PopulateGamesAsync(list, resolveArtwork: false);
        }

        SelectedGame = AllGames.FirstOrDefault(g => g.ExecutableName.Equals(Path.GetFileName(discovered.ExecutablePath), StringComparison.OrdinalIgnoreCase));
        ActivePage = LumaPage.GameDetails;
        ShowToast($"Added {discovered.Title} to My Games.");
    }

    private async Task AddLibraryFolderAsync()
    {
        string? folder = _dialogs.PickFolder("Select Game Library Directory");
        if (string.IsNullOrWhiteSpace(folder) || Settings.CustomLibraryPaths.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return;

        Settings.CustomLibraryPaths.Add(folder);
        CustomLibraryPaths.Add(folder);
        await _repo.SaveSettingsAsync(Settings);
        await ScanLibrariesAsync();
    }

    private async Task RemoveLibraryFolderAsync(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        Settings.CustomLibraryPaths.Remove(folder);
        CustomLibraryPaths.Remove(folder);
        await _repo.SaveSettingsAsync(Settings);
        await ScanLibrariesAsync();
    }

    private async Task PickCustomArtworkAsync(bool isHero)
    {
        if (SelectedGame == null) return;
        string? path = _dialogs.PickFile(
            isHero ? "Choose Custom Hero Banner" : "Choose Custom Portrait Cover",
            "Image Files (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp");
        if (string.IsNullOrWhiteSpace(path)) return;

        await _artworkService.SetCustomArtworkAsync(SelectedGame.Game, isHero ? null : path, isHero ? path : null);
        SelectedGame.NotifyArtworkUpdated();
        await _repo.SaveDiscoveredGamesAsync(AllGames.Select(c => c.Game));
        ShowToast($"Updated {(isHero ? "hero banner" : "cover artwork")} for {SelectedGame.Title}.");
    }

    private async Task RefreshAllArtworkAsync()
    {
        foreach (var card in AllGames)
        {
            await _artworkService.ResolveArtworkAsync(card.Game, Settings.EnableRemoteArtworkLookup);
            card.NotifyArtworkUpdated();
        }
        await _repo.SaveDiscoveredGamesAsync(AllGames.Select(c => c.Game));
        ShowToast("Artwork refreshed across your game library.");
    }

    private async Task ImportUserDllAsync()
    {
        string? file = _dialogs.PickFile("Locate Enhancement Component (.dll)", "NGX Components (*.dll)|*.dll");
        if (string.IsNullOrWhiteSpace(file)) return;

        try
        {
            var rec = await _dllVault.ImportDllAsync(file, SelectedNeuralImportModel);
            await ReloadCatalogsAsync();
            ShowToast($"Verified component {rec.FileName} v{rec.FileVersion}.");
        }
        catch (Exception ex)
        {
            ActionAlertMessage = $"Component validation failed: {ex.Message}";
        }
    }

    public async Task RefreshNeuralDllSectionAsync()
    {
        var reqManifest = _dllVault.GetNeuralRequirementsManifest();
        var modelReq = reqManifest.GetModelRequirement(SelectedNeuralImportModel);
        NeuralMinimumVersionRequired = modelReq.MinimumVersion;

        var nrManifest = Backends.FirstOrDefault(m => m.Id.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase));
        var hub = ProjectLuma.PackageManager.NeuralHubDetector.Detect(nrManifest?.StagedInstallPath);
        IsNeuralHubDetected = hub.IsDetected;
        NeuralHubExecutablePath = hub.ExecutablePath;

        var report = await _dllVault.VerifyImportedDllAsync("nvngx_dlssnr.dll", SelectedNeuralImportModel);
        bool hasVaulted = report.Status != DllValidationStatus.NotImported;
        HasImportedNeuralDll = hasVaulted;
        NeuralImportStatusText = hasVaulted ? "Imported" : "Not imported";
        NeuralDetectedVersion = string.IsNullOrWhiteSpace(report.DetectedVersion) ? "Not detected" : report.DetectedVersion;
        NeuralValidationStatusText = string.IsNullOrWhiteSpace(report.StatusLabel) ? report.Status.ToString() : report.StatusLabel;
        NeuralValidationDetailText = hasVaulted
            ? $"{report.Message} Vault: {_dllVault.NeuralRenderingVaultDirectory}"
            : "Select a legitimate NVIDIA Neural Rendering DLL that you are authorized to use. Project Luma will validate the file and check whether it is compatible with your selected Neural Rendering model.";

        NeuralLocalInstallationStatusText = hub.IsDetected
            ? (hasVaulted
                ? $"DLL vaulted + {hub.Summary}"
                : $"{hub.Summary} Import nvngx_dlssnr.dll to complete setup.")
            : (hasVaulted
                ? $"nvngx_dlssnr.dll v{NeuralDetectedVersion} vaulted; NeuralHub v0.9.0 handoff still required ({reqManifest.OfficialReleaseUrl})."
                : $"Not imported · {hub.Summary}");
    }

    private async Task ImportNvidiaDllAsync()
    {
        string? file = _dialogs.PickFile(
            "Import NVIDIA Neural Rendering DLL (nvngx_dlssnr.dll) — Select a legitimate copy you are authorized to use",
            "NVIDIA Neural Rendering DLL (nvngx_dlssnr.dll)|nvngx_dlssnr.dll|DLL Files (*.dll)|*.dll");
        if (string.IsNullOrWhiteSpace(file)) return;

        await ImportNvidiaDllFromPathAsync(file);
    }

    public async Task<DllValidationReport> ImportNvidiaDllFromPathAsync(string filePath)
    {
        var report = await _dllVault.ValidateDllAsync(filePath, SelectedNeuralImportModel);
        NeuralDetectedVersion = string.IsNullOrWhiteSpace(report.DetectedVersion) ? "Not detected" : report.DetectedVersion;
        NeuralValidationStatusText = report.StatusLabel;
        NeuralValidationDetailText = report.Message;

        if (!report.IsAcceptableForImport)
        {
            ActionAlertMessage = $"NVIDIA DLL validation failed ({report.StatusLabel}): {report.Message}";
            return report;
        }

        try
        {
            var rec = await _dllVault.ImportDllAsync(filePath, SelectedNeuralImportModel);
            await ReloadCatalogsAsync();
            await RefreshSelectedGameComponentsAsync();
            NeuralValidationStatusText = rec.HasAuthenticSignature ? "Imported successfully" : "Imported (signature verification unavailable)";
            ShowToast($"Imported {rec.FileName} v{rec.FileVersion} into DLL Vault.");
            return report;
        }
        catch (Exception ex)
        {
            ActionAlertMessage = $"Failed to store NVIDIA DLL: {ex.Message}";
            return report;
        }
    }

    private async Task ScanInstalledGamesForNeuralDllAsync()
    {
        var dirs = AllGames.Select(g => g.Game.InstallDirectory)
            .Concat(Settings.CustomLibraryPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await ScanDirectoriesForNeuralCandidatesAsync(dirs, "Installed Game");
    }

    private async Task ScanDownloadsForNeuralDllAsync()
    {
        string downloadsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        await ScanDirectoriesForNeuralCandidatesAsync(new[] { downloadsDir }, "Downloads");
    }

    public async Task ScanDirectoriesForNeuralCandidatesAsync(IEnumerable<string> roots, string categoryLabel)
    {
        _neuralScanCts?.Cancel();
        _neuralScanCts = new CancellationTokenSource();
        IsNeuralCandidateScanBusy = true;
        DiscoveredNeuralCandidates.Clear();
        NeuralCandidateScanSummary = $"Scanning {categoryLabel} (depth <= 3)...";

        try
        {
            var found = await _dllVault.DiscoverCandidateDllsAsync(roots, categoryLabel, SelectedNeuralImportModel, 3, _neuralScanCts.Token);
            foreach (var c in found)
                DiscoveredNeuralCandidates.Add(c);

            int compatibleCount = found.Count(c => c.CanImport);
            NeuralCandidateScanSummary = found.Count == 0
                ? $"No NVIDIA NGX DLLs found in {categoryLabel}."
                : $"Found {found.Count} candidate DLL(s) ({compatibleCount} compatible with {SelectedNeuralImportModel} model). Click Use This File to import.";
            ShowToast(NeuralCandidateScanSummary);
        }
        catch (OperationCanceledException)
        {
            NeuralCandidateScanSummary = "DLL discovery scan cancelled.";
        }
        catch (Exception ex)
        {
            NeuralCandidateScanSummary = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsNeuralCandidateScanBusy = false;
        }
    }

    private async Task UseDiscoveredNeuralCandidateAsync(DiscoveredDllCandidate? candidate)
    {
        if (candidate == null || string.IsNullOrWhiteSpace(candidate.SourcePath)) return;
        await ImportNvidiaDllFromPathAsync(candidate.SourcePath);
        DiscoveredSetupCandidates.Clear();
    }

    private void OpenDllVaultFolder()
    {
        Directory.CreateDirectory(_dllVault.NeuralRenderingVaultDirectory);
        _dialogs.OpenFolder(_dllVault.NeuralRenderingVaultDirectory);
    }

    private async Task VerifyImportedNeuralDllAsync()
    {
        var report = await _dllVault.VerifyImportedDllAsync("nvngx_dlssnr.dll", SelectedNeuralImportModel);
        NeuralDetectedVersion = string.IsNullOrWhiteSpace(report.DetectedVersion) ? "Not detected" : report.DetectedVersion;
        NeuralValidationStatusText = report.StatusLabel;
        NeuralValidationDetailText = report.Message;
        HasImportedNeuralDll = report.Status != DllValidationStatus.NotImported;
        NeuralImportStatusText = HasImportedNeuralDll ? "Imported" : "Not imported";

        if (report.IsAcceptableForImport)
            ShowToast($"Verified nvngx_dlssnr.dll v{report.DetectedVersion}: {report.StatusLabel}");
        else
            ActionAlertMessage = $"DLL verification reported {report.StatusLabel}: {report.Message}";
    }

    private async Task RemoveImportedNeuralDllAsync()
    {
        await _dllVault.DeleteDllAsync("nvngx_dlssnr.dll");
        await ReloadCatalogsAsync();
        await RefreshSelectedGameComponentsAsync();
        ShowToast("Removed imported nvngx_dlssnr.dll from DLL Vault.");
    }

    private void LaunchOrOpenNeuralHub()
    {
        if (IsNeuralHubDetected && !string.IsNullOrWhiteSpace(NeuralHubExecutablePath) && File.Exists(NeuralHubExecutablePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NeuralHubExecutablePath) { UseShellExecute = true });
                ShowToast("Launched NeuralHub. Import nvngx_dlssnr.dll inside NeuralHub.");
                return;
            }
            catch { }
        }

        string url = _dllVault.GetNeuralRequirementsManifest().OfficialReleaseUrl;
        _dialogs.OpenUrl(url);
        StartDownloadsFolderWatcher();
        ShowToast("Opened official DLSS-NR-on-AMD v0.9.0 release page.");
    }

    private async Task DeleteUserDllAsync(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        await _dllVault.DeleteDllAsync(fileName);
        await ReloadCatalogsAsync();
        ShowToast($"Removed {fileName}.");
    }

    private async Task ScanGameLibrariesForDllsAsync()
    {
        var dirs = AllGames.Select(g => g.Game.InstallDirectory)
            .Concat(Settings.CustomLibraryPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int count = await _componentResolver.AutoDiscoverDownloadedComponentsAsync(additionalGameDirs: dirs);
        await ReloadCatalogsAsync();
        await RefreshSelectedGameComponentsAsync();
        ShowToast(count > 0
            ? $"Found and verified {count} compatible component(s) from game folders & Downloads."
            : "No new compatible components found in game folders or Downloads.");
    }

    private async Task PlayGameAsync(GameCardViewModel? card)
    {
        if (card == null) return;
        if (IsSetupRunning)
        {
            ActionAlertMessage = "Please wait for the active enhancement setup to finish before launching a game.";
            return;
        }

        var progress = new Progress<GameLaunchPhase>(phase => card.LaunchPhase = phase);
        var result = await _launchService.LaunchGameAsync(card.Game, card.Profile, progress);

        if (!result.Succeeded)
        {
            ActionAlertMessage = result.Message;
            return;
        }

        ShowToast(result.Message);

        if (Settings.ShowInGameNotification && Application.Current != null)
        {
            try
            {
                var overlay = new LumaLaunchOverlayWindow();
                _ = overlay.ShowNotificationAsync(card.Title, result.ActiveProfileName, result.ActiveEnhancementsSummary);
            }
            catch
            {
                // Ignore headless/dispatcher shutdown edge cases
            }
        }
    }

    private async Task DownloadBackendAsync(BackendComponentItem? item)
    {
        if (item == null) return;

        // Route bundled child components (e.g., FSR 4 / XeSS / OptiFG) directly through authorized parent package (OptiScaler)
        var targetItem = item;
        if (item.IsBundledWithParent && !string.IsNullOrWhiteSpace(item.Manifest.ParentComponentId))
        {
            var parentItem = ComponentItems.FirstOrDefault(c => c.Id.Equals(item.Manifest.ParentComponentId, StringComparison.OrdinalIgnoreCase));
            if (parentItem != null)
                targetItem = parentItem;
        }

        if (targetItem.IsBusy) return;
        var manifest = targetItem.Manifest;

        if (targetItem.IsHardwareIncompatible)
        {
            ActionAlertMessage = $"{manifest.Name} is not compatible with {GpuInfo.ModelName} ({GpuInfo.Architecture}).";
            return;
        }

        if (targetItem.IsDriverManaged)
        {
            _dialogs.OpenUrl(manifest.OfficialReleasesPageUrl);
            ShowToast("Opened AMD Software guidance page.");
            return;
        }

        if (!manifest.LicenseRedistributionAllowed)
        {
            _dialogs.OpenUrl(manifest.OfficialReleasesPageUrl);
            StartDownloadsFolderWatcher();
            ShowToast("Opened official GitHub release page. Watching Downloads folder automatically.");
            return;
        }

        var ct = targetItem.BeginOperation();
        var linkedUpdateItem = UpdateItems.FirstOrDefault(u => u.ComponentId.Equals(targetItem.Id, StringComparison.OrdinalIgnoreCase));
        if (linkedUpdateItem != null)
        {
            linkedUpdateItem.IsBusy = true;
            linkedUpdateItem.StateBadgeText = "Setting up";
            linkedUpdateItem.ErrorMessage = string.Empty;
        }

        var progress = new Progress<PackageOperationProgress>(p => RunOnUi(() =>
        {
            targetItem.ApplyProgress(p);
            if (linkedUpdateItem != null)
            {
                linkedUpdateItem.IsBusy = true;
                linkedUpdateItem.DownloadProgress = p.Percent;
                linkedUpdateItem.DownloadSpeedText = targetItem.DownloadSpeedText;
                linkedUpdateItem.DownloadedSizeText = targetItem.DownloadedSizeText;
                linkedUpdateItem.Status = p.Message;
                linkedUpdateItem.StateBadgeText = targetItem.InstallStateLabel;
            }
        }));

        try
        {
            targetItem.ApplyProgress(new PackageOperationProgress(ComponentInstallState.Downloading, 5, 0, 0, 0, "Checking latest official release..."));
            var rel = await _packageManager.CheckLatestReleaseAsync(manifest, Settings.UpdateChannel, ct);
            if (rel == null)
            {
                targetItem.CompleteOperation(false, $"No compatible release found for {manifest.Name}.");
                ActionAlertMessage = $"No compatible release found for {manifest.Name}.";
                return;
            }

            await _packageManager.DownloadAndStageDetailedAsync(manifest, rel, progress, ct);
            await ReloadCatalogsAsync();
            targetItem.CompleteOperation(true);
            await targetItem.RefreshStateFromManifestAsync(GpuInfo);
            foreach (var child in ComponentItems.Where(c => c.Manifest.ParentComponentId.Equals(targetItem.Id, StringComparison.OrdinalIgnoreCase)))
                await child.RefreshStateFromManifestAsync(GpuInfo, targetItem.Manifest);
            if (linkedUpdateItem != null)
            {
                linkedUpdateItem.IsBusy = false;
                linkedUpdateItem.DownloadProgress = 100;
                linkedUpdateItem.InstalledVersion = rel.TagName;
                linkedUpdateItem.StateBadgeText = "Installed";
                linkedUpdateItem.Status = $"Verified local package ({rel.TagName}) ready for game profiles";
                linkedUpdateItem.ActionLabel = "Verify";
            }
            ShowToast($"Installed and verified {manifest.Name} {rel.TagName}.");
        }
        catch (OperationCanceledException)
        {
            targetItem.CompleteOperation(false);
            targetItem.RefreshStateFromManifest(GpuInfo);
            if (linkedUpdateItem != null)
            {
                linkedUpdateItem.IsBusy = false;
                linkedUpdateItem.DownloadProgress = 0;
                linkedUpdateItem.DownloadSpeedText = string.Empty;
                linkedUpdateItem.DownloadedSizeText = string.Empty;
                linkedUpdateItem.StateBadgeText = targetItem.InstallStateLabel;
                linkedUpdateItem.Status = "Download cancelled cleanly";
            }
            ShowToast($"Cancelled {manifest.Name} download cleanly.");
        }
        catch (Exception ex)
        {
            _logger.Error("UI", $"Component download failed for {manifest.Id}", ex);
            targetItem.CompleteOperation(false, ex.Message);
            if (linkedUpdateItem != null)
            {
                linkedUpdateItem.IsBusy = false;
                linkedUpdateItem.StateBadgeText = "Requires attention";
                linkedUpdateItem.Status = "Download failed — retry";
                linkedUpdateItem.ErrorMessage = ex.Message;
            }
            ActionAlertMessage = $"{manifest.Name} download failed: {ex.Message}";
        }
    }

    private async Task ConnectLocalBackendAsync(BackendComponentItem? item)
    {
        if (item == null || item.IsBusy) return;
        string? folder = _dialogs.PickFolder($"Select Existing {item.Name} Installation Folder");
        if (string.IsNullOrWhiteSpace(folder)) return;

        try
        {
            await _packageManager.StageLocalArchiveAsync(item.Manifest, folder, item.Manifest.PinnedStableVersion);
            await ReloadCatalogsAsync();
            await RefreshSelectedGameComponentsAsync();
            ShowToast($"Connected existing installation for {item.Name}.");
        }
        catch (Exception ex)
        {
            ActionAlertMessage = $"Could not connect folder: {ex.Message}";
        }
    }

    private async Task ImportLocalBackendAsync(BackendComponentItem? item)
    {
        if (item == null || item.IsBusy) return;
        var manifest = item.Manifest;
        string? path = _dialogs.PickFile($"Select Package Archive for {manifest.Name}", "Archives & Installers (*.zip;*.7z;*.exe)|*.zip;*.7z;*.exe");
        if (string.IsNullOrWhiteSpace(path)) return;

        var ct = item.BeginOperation();
        try
        {
            item.ApplyProgress(new PackageOperationProgress(ComponentInstallState.Installing, 50, 0, 0, 0, "Extracting local archive cleanly..."));
            await _packageManager.StageLocalArchiveAsync(manifest, path, manifest.PinnedStableVersion, ct);
            item.CompleteOperation(true);
            await ReloadCatalogsAsync();
            ShowToast($"Prepared local package for {manifest.Name}.");
        }
        catch (Exception ex)
        {
            item.CompleteOperation(false, ex.Message);
            ActionAlertMessage = $"Package import failed: {ex.Message}";
        }
    }

    private async Task RollbackBackendAsync(BackendComponentItem? item)
    {
        if (item == null || item.IsBusy) return;
        bool ok = await _packageManager.RollbackBackendAsync(item.Id);
        await ReloadCatalogsAsync();
        if (ok) ShowToast($"Restored previous version of {item.Name}.");
        else ActionAlertMessage = $"No previous rollback version exists for {item.Name}.";
    }

    private async Task VerifyBackendAsync(BackendComponentItem? item)
    {
        if (item == null || item.IsBusy) return;
        string targetId = item.IsBundledWithParent && !string.IsNullOrWhiteSpace(item.Manifest.ParentComponentId)
            ? item.Manifest.ParentComponentId
            : item.Id;
        bool ok = await _packageManager.VerifyBackendIntegrityAsync(targetId);
        if (ok) ShowToast($"{item.Name} integrity verified.");
        else ActionAlertMessage = $"{item.Name} is not installed yet or failed integrity check.";
    }

    private async Task RepairBackendAsync(BackendComponentItem? item)
    {
        if (item == null || item.IsBusy) return;
        string targetId = item.IsBundledWithParent && !string.IsNullOrWhiteSpace(item.Manifest.ParentComponentId)
            ? item.Manifest.ParentComponentId
            : item.Id;
        var targetItem = ComponentItems.FirstOrDefault(c => c.Id.Equals(targetId, StringComparison.OrdinalIgnoreCase)) ?? item;

        bool ok = await _packageManager.VerifyBackendIntegrityAsync(targetId);
        if (ok)
        {
            ShowToast($"{item.Name} passed SHA-256 verification — no repair needed.");
            return;
        }

        if (targetItem.CanDownload)
        {
            await DownloadBackendAsync(targetItem);
        }
        else
        {
            ActionAlertMessage = $"{item.Name} failed integrity verification and requires re-acquiring from its official source.";
        }
    }

    private async Task RemoveBackendAsync(BackendComponentItem? item)
    {
        if (item == null || item.IsBusy) return;
        await _packageManager.RemoveBackendAsync(item.Id);
        await ReloadCatalogsAsync();
        ShowToast($"Removed {item.Name}.");
    }

    private async Task VerifySelectedGameHealthAsync()
    {
        if (SelectedGame == null) return;
        var health = await _installer.VerifyInstallationHealthAsync(SelectedGame.Game);
        SelectedGame.Game.HealthState = health;
        OnPropertyChanged(nameof(SelectedGame));
        if (health == IntegrationHealthState.Healthy || health == IntegrationHealthState.NotInstalled)
            ShowToast($"{SelectedGame.Title} files verified ({health}).");
        else
            ActionAlertMessage = $"{SelectedGame.Title} integrity check reported {health}. Click Apply Recommended Enhancements or Restore Original Game Files.";
    }

    private async Task UninstallSelectedGameAsync()
    {
        if (SelectedGame == null) return;
        try
        {
            bool ok = await _installer.UninstallAsync(SelectedGame.Game);
            CapabilityEngine.PopulateFriendlyStatuses(SelectedGame.Game, SelectedGame.Profile);
            SelectedGame.NotifyStatusUpdated();
            await _repo.SaveDiscoveredGamesAsync(AllGames.Select(a => a.Game));
            await LoadSelectedGameBackupsAsync();
            OnPropertyChanged(nameof(SelectedGame));
            OnPropertyChanged(nameof(ReadyGamesCount));
            OnPropertyChanged(nameof(EnhancedGamesCount));
            if (ok) ShowToast($"Restored original files for {SelectedGame.Title}.");
            else ActionAlertMessage = "No Project Luma backup session was found for this game.";
        }
        catch (Exception ex)
        {
            ActionAlertMessage = $"Could not restore original game files: {ex.Message}";
        }
    }

    private async Task LoadSelectedGameBackupsAsync()
    {
        SelectedGameBackups.Clear();
        if (SelectedGame == null) return;
        var sessions = await _installer.GetBackupSessionsAsync(SelectedGame.Game.ExecutableDirectory);
        foreach (var s in sessions) SelectedGameBackups.Add(s);
    }

    private async Task ApplyUpdateItemAsync(UpdateCheckItem? item)
    {
        if (item == null || item.IsBusy || item.Kind != "Backend" || !item.ShowActionButton) return;
        try
        {
            var componentItem = ComponentItems.FirstOrDefault(c => c.Id.Equals(item.ComponentId, StringComparison.OrdinalIgnoreCase));

            if (item.ActionLabel.Equals("Verify", StringComparison.OrdinalIgnoreCase))
            {
                await VerifyBackendAsync(componentItem);
                return;
            }

            if (item.ActionLabel.Equals("Retry Check", StringComparison.OrdinalIgnoreCase))
            {
                await _updateCoordinator.CheckNowAsync();
                return;
            }

            if (item.ActionLabel.Equals("Official Release", StringComparison.OrdinalIgnoreCase) ||
                item.ActionLabel.Equals("Adrenalin Guide", StringComparison.OrdinalIgnoreCase) ||
                item.ActionLabel.Equals("Check GitHub", StringComparison.OrdinalIgnoreCase))
            {
                if (componentItem != null)
                {
                    _dialogs.OpenUrl(componentItem.OfficialReleasesPageUrl);
                    if (!componentItem.IsDriverManaged)
                        StartDownloadsFolderWatcher();
                    ShowToast(componentItem.IsDriverManaged
                        ? "Opened AMD Software: Adrenalin Edition guidance."
                        : "Opened official GitHub release. Watching Downloads folder automatically.");
                }
                return;
            }

            if (componentItem != null && componentItem.CanDownload)
            {
                await DownloadBackendAsync(componentItem);
                return;
            }

            await _updateCoordinator.ApplyBackendUpdateAsync(item.ComponentId);
            await ReloadCatalogsAsync();
            ShowToast($"Updated {item.ComponentName}.");
        }
        catch (Exception ex)
        {
            ActionAlertMessage = $"Component action failed: {ex.Message}";
        }
    }

    public void StartDownloadsFolderWatcher(string? overrideDirectory = null)
    {
        string dir = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : overrideDirectory;
        if (!Directory.Exists(dir)) return;

        StopDownloadsFolderWatcher();
        try
        {
            _downloadsWatcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                EnableRaisingEvents = true
            };
            _downloadsWatcher.Created += (_, _) => ScheduleDownloadsDiscovery(dir);
            _downloadsWatcher.Renamed += (_, _) => ScheduleDownloadsDiscovery(dir);
            _downloadsWatcher.Changed += (_, _) => ScheduleDownloadsDiscovery(dir);
            IsDownloadsWatcherActive = true;
        }
        catch
        {
            IsDownloadsWatcherActive = false;
        }
    }

    public void StopDownloadsFolderWatcher()
    {
        _downloadsDebounceCts?.Cancel();
        _downloadsDebounceCts?.Dispose();
        _downloadsDebounceCts = null;
        if (_downloadsWatcher != null)
        {
            _downloadsWatcher.EnableRaisingEvents = false;
            _downloadsWatcher.Dispose();
            _downloadsWatcher = null;
        }
        IsDownloadsWatcherActive = false;
    }

    private async void ScheduleDownloadsDiscovery(string downloadsDir)
    {
        _downloadsDebounceCts?.Cancel();
        _downloadsDebounceCts = new CancellationTokenSource();
        var token = _downloadsDebounceCts.Token;

        try
        {
            await Task.Delay(500, token);
            if (_disposed || token.IsCancellationRequested) return;

            var gameDirs = AllGames.Select(g => g.Game.InstallDirectory)
                .Concat(Settings.CustomLibraryPaths)
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .ToList();

            int found = await _componentResolver.AutoDiscoverDownloadedComponentsAsync(downloadsDir, gameDirs, token);
            if (found > 0 && !_disposed && !token.IsCancellationRequested)
            {
                RunOnUi(async () =>
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    await ReloadCatalogsAsync();
                    await RefreshSelectedGameComponentsAsync();
                    ShowToast($"Detected & verified {found} downloaded component(s) automatically.");
                });
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        StopDownloadsFolderWatcher();
        _scanCts?.Cancel();
        _setupCts?.Cancel();
        _neuralScanCts?.Cancel();
        _toastCts?.Cancel();
    }

    private async Task SaveSettingsAsync()
    {
        if (_startupService is not InMemoryStartupServiceStub)
        {
            try
            {
                if (Settings.LaunchOnWindowsStartup)
                {
                    string? exePath = Environment.ProcessPath;
                    if (string.IsNullOrWhiteSpace(exePath) || Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string candidate = Path.Combine(AppContext.BaseDirectory, "ProjectLuma.exe");
                        if (File.Exists(candidate)) exePath = candidate;
                    }
                    if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    {
                        _startupService.Enable(exePath);
                        Settings.LaunchOnWindowsStartup = _startupService.IsEnabled();
                    }
                }
                else
                {
                    _startupService.Disable();
                    Settings.LaunchOnWindowsStartup = _startupService.IsEnabled();
                }
            }
            catch (Exception ex)
            {
                Settings.LaunchOnWindowsStartup = _startupService.IsEnabled();
                ActionAlertMessage = $"Could not update Windows Startup setting: {ex.Message}";
            }
            UpdateWindowsStartupStatusDisplay(Settings.LaunchOnWindowsStartup);
        }
        else
        {
            UpdateWindowsStartupStatusDisplay(Settings.LaunchOnWindowsStartup);
        }

        _trayController.SyncPreference(Settings.MinimizeToTray);
        TrayPreferenceSyncRequested?.Invoke();
        await _repo.SaveSettingsAsync(Settings);
        OnPropertyChanged(nameof(Settings));
        ShowToast("Preferences saved.");
    }

    private void UpdateWindowsStartupStatusDisplay(bool enabled)
    {
        WindowsStartupStatusText = enabled
            ? "Registered to launch at Windows sign-in (HKCU\\...\\Run\\ProjectLuma)"
            : "Not registered for Windows startup";
    }

    private async Task ExportHardwareProfileAsync()
    {
        string? target = _dialogs.SaveFile("Export System Capability Profile", "luma-system-profile.json", "JSON files (*.json)|*.json");
        if (string.IsNullOrWhiteSpace(target)) return;
        await File.WriteAllTextAsync(target, JsonSerializer.Serialize(HardwareProfile, new JsonSerializerOptions { WriteIndented = true }));
        ShowToast("System profile exported.");
    }

    private void CompleteOnboarding()
    {
        IsOnboardingVisible = false;
        if (!Settings.HasCompletedOnboarding)
        {
            Settings.HasCompletedOnboarding = true;
            _ = _repo.SaveSettingsAsync(Settings);
        }
    }

    private void RefreshSelectedGameContext()
    {
        OnPropertyChanged(nameof(AvailableUpscalingPresets));
        OnPropertyChanged(nameof(AvailableFrameGenEngines));
    }

    private void ApplyUpdateStatus(UpdateCoordinatorStatus status)
    {
        UpdateStatus = status;
        var busyById = UpdateItems
            .Where(u => u.IsBusy)
            .ToDictionary(u => u.ComponentId, StringComparer.OrdinalIgnoreCase);

        UpdateItems.Clear();
        foreach (var item in status.Items)
        {
            if (busyById.TryGetValue(item.ComponentId, out var activeBusy))
                UpdateItems.Add(activeBusy);
            else
                UpdateItems.Add(item);
        }
        OnPropertyChanged(nameof(PendingUpdatesCount));
    }

    public async void ShowToast(string message)
    {
        _toastCts?.Cancel();
        _toastCts = new CancellationTokenSource();
        var token = _toastCts.Token;

        ToastMessage = message;
        IsToastVisible = true;
        try
        {
            await Task.Delay(3500, token);
            if (!token.IsCancellationRequested)
                IsToastVisible = false;
        }
        catch (TaskCanceledException) { }
    }

    private static void RunOnUi(Action action)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null || disp.HasShutdownStarted || disp.HasShutdownFinished || !disp.Thread.IsAlive || disp.CheckAccess())
            action();
        else
            disp.BeginInvoke(action);
    }
}

public sealed class ConcurrentAsyncRelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    public ConcurrentAsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }
    public event EventHandler? CanExecuteChanged
    {
        add => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => _canExecute == null || _canExecute((T?)parameter);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            await _execute((T?)parameter);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }
}

public sealed class AsyncRelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Func<T?, Task> _execute;
    private bool _running;
    public AsyncRelayCommand(Func<T?, Task> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running;
    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute((T?)parameter); }
        catch { }
        finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}

internal sealed class InMemoryStartupServiceStub : IWindowsStartupService
{
    private string? _cmd;
    public string RegistryKeyPath => @"Software\Microsoft\Windows\CurrentVersion\Run";
    public string ValueName => "ProjectLuma";
    public bool IsEnabled() => !string.IsNullOrWhiteSpace(_cmd);
    public string? GetRegisteredCommand() => _cmd;
    public void Enable(string executablePath) => _cmd = $"\"{executablePath}\"";
    public void Disable() => _cmd = null;
    public void UpdateExecutablePathIfEnabled(string newExecutablePath)
    {
        if (IsEnabled()) Enable(newExecutablePath);
    }
}

public sealed class RelayCommand<T> : System.Windows.Input.ICommand
{
    private readonly Action<T?> _execute;
    public RelayCommand(Action<T?> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute((T?)parameter);
}
