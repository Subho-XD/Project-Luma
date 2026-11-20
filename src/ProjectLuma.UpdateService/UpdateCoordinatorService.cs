using Microsoft.Extensions.Hosting;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.UpdateService;

/// <summary>Self-maintaining background synchronization loop coordinating app, backend, and per-game integration updates.</summary>
public sealed class UpdateCoordinatorService : BackgroundService, IUpdateCoordinator
{
    private readonly IGpuDetector _gpuDetector;
    private readonly ICompatibilityRepository _repo;
    private readonly IPackageManager _packageManager;
    private readonly IBackendOrchestrator _orchestrator;
    private readonly IGameInstaller _installer;
    private readonly ILumaLogger _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public UpdateCoordinatorStatus Status { get; } = new();
    public event Action<UpdateCoordinatorStatus>? StatusChanged;

    private AppSelfUpdateManager? _selfUpdateManager;
    private HttpClient? _appHttpClient;
    private string? _releasesApiUrlOverride;
    private string? _installDirOverride;
    private string? _appVersionOverride;

    internal void ConfigureAppSelfUpdater(
        HttpClient? httpClient = null,
        string? releasesApiUrl = null,
        string? currentInstallDirectory = null,
        string? appVersionOverride = null)
    {
        _appHttpClient = httpClient;
        _releasesApiUrlOverride = releasesApiUrl;
        _installDirOverride = currentInstallDirectory;
        _appVersionOverride = appVersionOverride;
        _selfUpdateManager = new AppSelfUpdateManager(_logger, httpClient, releasesApiUrl, currentInstallDirectory, appVersionOverride);
    }

    private AppSelfUpdateManager GetSelfUpdateManager()
        => _selfUpdateManager ??= new AppSelfUpdateManager(_logger, _appHttpClient, _releasesApiUrlOverride, _installDirOverride, _appVersionOverride);

    public Task<AppSelfUpdateStageResult> DownloadAndStageAppUpdateAsync(UpdateCheckItem item, CancellationToken ct = default)
        => GetSelfUpdateManager().DownloadAndStageAsync(item, ct);

    public bool LaunchSelfUpdater(AppSelfUpdateStageResult stageResult)
        => GetSelfUpdateManager().LaunchUpdater(stageResult);

    public UpdateCoordinatorService(
        IGpuDetector gpuDetector,
        ICompatibilityRepository repo,
        IPackageManager packageManager,
        IBackendOrchestrator orchestrator,
        IGameInstaller installer,
        ILumaLogger logger)
    {
        _gpuDetector = gpuDetector;
        _repo = repo;
        _packageManager = packageManager;
        _orchestrator = orchestrator;
        _installer = installer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await _repo.LoadSettingsAsync(stoppingToken);
            if (settings.AutoCheckUpdates)
            {
                await CheckNowAsync(stoppingToken);
            }

            TimeSpan interval = TimeSpan.FromHours(Math.Clamp(settings.UpdateCheckIntervalHours, 1, 168));
            Status.NextScheduledCheckUtc = DateTime.UtcNow.Add(interval);
            NotifyStatus();

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task CheckNowAsync(CancellationToken ct = default)
    {
        if (!await _syncLock.WaitAsync(0, ct)) return;

        try
        {
            Status.IsChecking = true;
            Status.CurrentPhase = "Detecting GPU and loading backend registry";
            Status.ProgressPercent = 10;
            NotifyStatus();

            var settings = await _repo.LoadSettingsAsync(ct);
            var gpu = await _gpuDetector.DetectPrimaryGpuAsync(ct);
            var manifests = await _packageManager.GetManifestsAsync(ct);
            var userDlls = await _repo.LoadUserDllsAsync(ct);

            var appItem = await GetSelfUpdateManager().CheckForAppUpdateAsync(settings.AppVersion, settings.UpdateChannel, ct);
            var items = new List<UpdateCheckItem> { appItem };

            bool anyNetworkFailure = false;
            int total = Math.Max(1, manifests.Count);

            for (int i = 0; i < manifests.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var m = manifests[i];
                Status.CurrentPhase = $"Checking upstream releases for {m.Name}";
                Status.ProgressPercent = 20 + (int)(((i + 1) / (double)total) * 55);
                NotifyStatus();

                bool archCompatible = m.SupportedArchitectures.Contains(gpu.Architecture.ToString(), StringComparer.OrdinalIgnoreCase);
                string stagedPath = m.StagedInstallPath;
                string installedVersion = m.CurrentInstalledVersion;

                if (m.PreferredInstallMethod.Equals("BundledWithParent", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(m.ParentComponentId))
                {
                    continue; // Bundled capabilities appear inside Enhancement Library under their category and update with OptiScaler
                }

                bool hasDiskFiles = !string.IsNullOrWhiteSpace(stagedPath) &&
                                    Directory.Exists(stagedPath) &&
                                    Directory.EnumerateFiles(stagedPath, "*", SearchOption.AllDirectories).Any();
                if (!hasDiskFiles) installedVersion = "Not Installed";

                var item = new UpdateCheckItem
                {
                    ComponentId = m.Id,
                    ComponentName = m.Name,
                    Kind = "Backend",
                    Category = m.FeatureCategory,
                    LicenseSpdx = m.LicenseSpdx,
                    InstalledVersion = installedVersion,
                    AvailableVersion = settings.UpdateChannel == UpdateChannel.Experimental ? m.PinnedExperimentalVersion : m.PinnedStableVersion,
                    VerificationLevel = m.VerificationLevel,
                    CanRollback = hasDiskFiles
                };

                if (!archCompatible)
                {
                    item.StateBadgeText = "Not compatible";
                    item.Status = $"Excluded on {gpu.Architecture}";
                    item.ReasonText = m.CompatibilityStatus;
                    item.ActionLabel = string.Empty;
                    item.CanApply = false;
                    items.Add(item);
                    continue;
                }

                if (m.PreferredInstallMethod.Equals("GuidedDriverAdvisor", StringComparison.OrdinalIgnoreCase))
                {
                    item.InstalledVersion = gpu.MeetsAdrenalin2611Minimum ? gpu.AdrenalinVersionDisplay : "Adrenalin Required";
                    item.StateBadgeText = gpu.MeetsAdrenalin2611Minimum ? "Driver managed" : "Requires attention";
                    item.Status = "Managed by AMD Software: Adrenalin Edition";
                    item.ReasonText = "Configured directly through AMD Adrenalin driver settings (never modifies registry keys).";
                    item.ActionLabel = "Adrenalin Guide";
                    item.CanApply = true;
                    items.Add(item);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(m.OfficialReleaseApiUrl))
                {
                    item.StateBadgeText = hasDiskFiles ? "Installed" : "Requires attention";
                    item.Status = hasDiskFiles ? $"Linked local installation ({installedVersion})" : m.CompatibilityStatus;
                    item.ReasonText = m.InstallationInstructions;
                    item.ActionLabel = hasDiskFiles ? "Verify" : "Official Release";
                    item.CanApply = true;
                    items.Add(item);
                    continue;
                }

                try
                {
                    var release = await _packageManager.CheckLatestReleaseAsync(m, settings.UpdateChannel, ct);
                    if (release != null)
                    {
                        item.AvailableVersion = release.TagName;
                        bool isUpdate = !hasDiskFiles ||
                                        !string.Equals(installedVersion, release.TagName, StringComparison.OrdinalIgnoreCase);

                        if (!m.LicenseRedistributionAllowed)
                        {
                            item.StateBadgeText = hasDiskFiles ? (isUpdate ? "Needs update" : "Installed") : "Guided setup";
                            item.Status = hasDiskFiles
                                ? (isUpdate
                                    ? $"Local {installedVersion} installed · {release.TagName} on GitHub"
                                    : $"Linked local installation ({installedVersion})")
                                : $"Upstream {release.TagName} on GitHub (Personal non-commercial license)";
                            item.ReasonText = "Custom license forbids third-party bundling. Open official release and Project Luma detects your download automatically.";
                            item.ActionLabel = "Official Release";
                            item.CanApply = true;
                        }
                        else if (!hasDiskFiles)
                        {
                            item.StateBadgeText = "Ready to configure";
                            item.Status = $"Verified {release.TagName} ready for automated download & game setup";
                            item.ActionLabel = "Download Official";
                            item.CanApply = true;

                            if (settings.AutoDownloadVerifiedUpdates && m.Id == "optiscaler")
                            {
                                Status.CurrentPhase = $"Downloading verified release {release.TagName} for {m.Name}";
                                NotifyStatus();
                                var progress = new Progress<double>(pct => item.DownloadProgress = pct);
                                await _packageManager.DownloadAndStageAsync(m, release, progress, ct);
                                item.InstalledVersion = release.TagName;
                                item.StateBadgeText = "Installed";
                                item.Status = $"Verified local package ({release.TagName}) ready for game profiles";
                                item.ActionLabel = "Verify";
                                item.CanApply = true;

                                if (settings.AutoUpdateGameIntegrations)
                                    await UpdateEligibleGamesForBackendAsync(m.Id, ct);
                            }
                        }
                        else if (isUpdate)
                        {
                            item.StateBadgeText = "Needs update";
                            item.Status = $"Update available ({installedVersion} -> {release.TagName})";
                            item.ActionLabel = "Update";
                            item.CanApply = true;
                        }
                        else
                        {
                            item.StateBadgeText = "Installed";
                            item.Status = $"Verified local package ({installedVersion}) ready for game profiles";
                            item.ActionLabel = "Verify";
                            item.CanApply = true;
                        }
                    }
                    else
                    {
                        item.StateBadgeText = hasDiskFiles ? "Installed" : "Ready to configure";
                        item.Status = hasDiskFiles ? $"Local package ({installedVersion})" : "Ready when online release check succeeds";
                        item.ActionLabel = hasDiskFiles ? "Verify" : "Official Release";
                        item.CanApply = true;
                    }
                }
                catch (Exception ex)
                {
                    anyNetworkFailure = true;
                    item.StateBadgeText = hasDiskFiles ? "Installed" : "Offline";
                    item.Status = hasDiskFiles ? $"Local package ({installedVersion}) · Offline" : "Offline / Release check deferred";
                    item.ReasonText = ex.Message;
                    item.ErrorMessage = ex.Message;
                    item.ActionLabel = hasDiskFiles ? "Verify" : (m.LicenseRedistributionAllowed ? "Retry Check" : "Official Release");
                    item.CanApply = true;
                    _logger.Warn("UpdateService", $"Release check skipped for {m.Id} (offline resilience active)", ex.Message);
                }

                items.Add(item);
            }

            Status.IsOffline = anyNetworkFailure;
            if (!anyNetworkFailure)
                Status.LastSuccessfulCheckUtc = DateTime.UtcNow;

            Status.Items = items;
            Status.CurrentPhase = anyNetworkFailure ? "Completed (Offline fallback)" : "Synchronized";
            Status.ProgressPercent = 100;
        }
        finally
        {
            Status.IsChecking = false;
            NotifyStatus();
            _syncLock.Release();
        }
    }

    public async Task ApplyBackendUpdateAsync(string backendId, CancellationToken ct = default)
    {
        var settings = await _repo.LoadSettingsAsync(ct);
        var manifests = await _packageManager.GetManifestsAsync(ct);
        var manifest = manifests.FirstOrDefault(m => m.Id.Equals(backendId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unknown backend '{backendId}'.");

        if (!manifest.LicenseRedistributionAllowed)
            throw new InvalidOperationException($"{manifest.Name} forbids automated third-party repackaging under its license. Open {manifest.OfficialReleasesPageUrl} or stage your downloaded local package.");

        var release = await _packageManager.CheckLatestReleaseAsync(manifest, settings.UpdateChannel, ct)
            ?? throw new InvalidOperationException("No compatible release returned from official GitHub endpoint.");

        await _packageManager.DownloadAndStageAsync(manifest, release, null, ct);

        if (settings.AutoUpdateGameIntegrations)
            await UpdateEligibleGamesForBackendAsync(backendId, ct);

        await CheckNowAsync(ct);
    }

    public async Task UpdateEligibleGamesForBackendAsync(string backendId, CancellationToken ct = default)
    {
        var games = await _repo.LoadDiscoveredGamesAsync(ct);
        var manifests = await _packageManager.GetManifestsAsync(ct);
        var userDlls = await _repo.LoadUserDllsAsync(ct);
        var manifest = manifests.FirstOrDefault(m => m.Id.Equals(backendId, StringComparison.OrdinalIgnoreCase));
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.StagedInstallPath)) return;

        foreach (var game in games.Where(g => g.InstalledBackendId.Equals(backendId, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.Equals(game.InstalledBackendVersion, manifest.CurrentInstalledVersion, StringComparison.OrdinalIgnoreCase))
                continue;

            if (_installer.DetectRunningProcessesInDirectory(game.InstallDirectory).Count > 0)
            {
                _logger.Warn("UpdateService", $"Deferred game integration update for {game.Title} because game process is currently running.");
                continue;
            }

            var profile = await _repo.LoadGameProfileAsync(game.Id, ct);
            if (profile == null) continue;

            profile.TargetBackendVersion = manifest.CurrentInstalledVersion;
            var plan = _orchestrator.ComposeCombinedPlan(game, profile, manifests, userDlls);
            await _installer.InstallOrUpdateAsync(game, profile, plan, ct);
            await _repo.RecordUpdateHistoryAsync(game.Title, game.InstalledBackendVersion, manifest.CurrentInstalledVersion, "GameUpdated", "Updated game integration safely", ct);
        }

        await _repo.SaveDiscoveredGamesAsync(games, ct);
    }

    private void NotifyStatus() => StatusChanged?.Invoke(Status);
}
