using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.PackageManager;

/// <summary>Automatically inspects game/GPU needs, resolves permitted downloads, and guides any proprietary fallback.</summary>
public sealed class IntelligentComponentResolver : IIntelligentComponentResolver
{
    private readonly IPackageManager _packageManager;
    private readonly IUserDllVault _dllVault;
    private readonly ICompatibilityRepository _repo;
    private readonly IBackendOrchestrator _orchestrator;
    private readonly IGameInstaller _installer;
    private readonly ILumaLogger _logger;
    private readonly string? _defaultDownloadsDirectory;
    private readonly NeuralHubProbeOptions? _neuralHubProbeOptions;

    public IntelligentComponentResolver(
        IPackageManager packageManager,
        IUserDllVault dllVault,
        ICompatibilityRepository repo,
        IBackendOrchestrator orchestrator,
        IGameInstaller installer,
        ILumaLogger logger,
        string? defaultDownloadsDirectory = null,
        NeuralHubProbeOptions? neuralHubProbeOptions = null)
    {
        _packageManager = packageManager;
        _dllVault = dllVault;
        _repo = repo;
        _orchestrator = orchestrator;
        _installer = installer;
        _logger = logger;
        _defaultDownloadsDirectory = defaultDownloadsDirectory;
        _neuralHubProbeOptions = neuralHubProbeOptions;
    }

    public async Task<ComponentResolutionPlan> AnalyzeRequirementsAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        DetectedGpuInfo gpu,
        CancellationToken ct = default)
    {
        var manifests = await _packageManager.GetManifestsAsync(ct);
        var userDlls = await _dllVault.GetImportedDllsAsync(ct);
        var plan = new ComponentResolutionPlan { BackendId = "optiscaler" };

        // 1. Sharper Image / Smoother Gameplay (OptiScaler GPL-3.0)
        if (profile.EnableSuperResolution || profile.EnableFrameGeneration)
        {
            var opti = manifests.FirstOrDefault(m => m.Id == "optiscaler");
            bool staged = opti != null && !string.IsNullOrWhiteSpace(opti.StagedInstallPath) && Directory.Exists(opti.StagedInstallPath);

            plan.Components.Add(new ComponentRequirementItem
            {
                ComponentId = "optiscaler",
                FriendlyName = "Image Sharpening & Frame Smoothing Engine",
                PlainEnglishPurpose = "Enables FSR 4 / FSR 3.1 image sharpening and smoother frame pacing in supported games.",
                IsRequiredForSelectedProfile = true,
                IsSatisfied = staged,
                CanDownloadAutomatically = true,
                OfficialGuidanceUrl = opti?.OfficialReleasesPageUrl ?? "https://github.com/OptiScaler/OptiScaler/releases",
                ActionPrompt = staged ? "Ready locally" : "Downloads automatically from official GitHub release",
                TechnicalDetail = $"OptiScaler ({opti?.PinnedStableVersion ?? "v0.9.5"}, GPL-3.0, SignPath signed)"
            });
        }

        // 2. AI Visual Enhancement (DLSS-NR-on-AMD) when selected or evaluating Neural capabilities
        bool inspectNr = profile.EnableNeuralRendering || profile.NeuralRenderingState != CapabilityState.Unsupported;
        if (inspectNr)
        {
            if (profile.EnableNeuralRendering)
                plan.BackendId = "dlss-nr-on-amd";

            var nrManifest = manifests.FirstOrDefault(m => m.Id == "dlss-nr-on-amd");
            var existingNrVault = userDlls.FirstOrDefault(d => d.DllKind.Equals("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
            var hubReport = NeuralHubDetector.InspectFullStatus(nrManifest?.StagedInstallPath, existingNrVault, new[] { game }, _neuralHubProbeOptions);
            bool nrBackendStaged = (nrManifest != null && NeuralHubDetector.HasRuntimeBinaryPayload(nrManifest.StagedInstallPath)) || (hubReport.IsAppDetected && hubReport.IsRuntimeVerified);
            bool hasNrDllAny = existingNrVault != null || hubReport.IsDllImportedInNeuralHub;

            if (profile.EnableNeuralRendering && (!nrBackendStaged || !hasNrDllAny))
            {
                await AutoDiscoverDownloadedComponentsAsync(
                    additionalGameDirs: new[] { game.InstallDirectory, game.ExecutableDirectory },
                    ct: ct);
                manifests = await _packageManager.GetManifestsAsync(ct);
                nrManifest = manifests.FirstOrDefault(m => m.Id == "dlss-nr-on-amd");
                userDlls = await _dllVault.GetImportedDllsAsync(ct);
                existingNrVault = userDlls.FirstOrDefault(d => d.DllKind.Equals("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
                hubReport = NeuralHubDetector.InspectFullStatus(nrManifest?.StagedInstallPath, existingNrVault, new[] { game }, _neuralHubProbeOptions);
                nrBackendStaged = (nrManifest != null && NeuralHubDetector.HasRuntimeBinaryPayload(nrManifest.StagedInstallPath)) || (hubReport.IsAppDetected && hubReport.IsRuntimeVerified);
            }

            plan.Components.Add(new ComponentRequirementItem
            {
                ComponentId = "dlss-nr-on-amd",
                FriendlyName = "AI NeuralHub Runtime (User-Acquired Upstream)",
                PlainEnglishPurpose = "Experimental AMD Neural Rendering integration package distributed directly by the author under personal non-commercial terms.",
                IsRequiredForSelectedProfile = profile.EnableNeuralRendering,
                IsSatisfied = nrBackendStaged,
                CanDownloadAutomatically = false,
                OfficialGuidanceUrl = nrManifest?.OfficialReleasesPageUrl ?? "https://github.com/danielblnc/DLSS-NR-on-AMD/releases",
                ActionPrompt = nrBackendStaged ? hubReport.StatusHeadline : "Open official GitHub release; install NeuralHub v0.9.0",
                TechnicalDetail = hubReport.IsAppDetected ? hubReport.DiagnosticsSummary : (nrBackendStaged ? $"Staged cleanly at {nrManifest?.StagedInstallPath}" : "Not bundled due to personal non-commercial license restrictions; source-only archives without runtime binaries do not satisfy this step")
            });

            if (gpu.Architecture == GpuArchitecture.RDNA2)
            {
                string hipNote = gpu.Hip72OfficiallySupported
                    ? "Supported AMD HIP runtime"
                    : "Official AMD HIP SDK 7.2 Windows docs exclude RDNA 2 (gfx1032); community DLSS-NR-on-AMD reports experimental HIP support.";
                plan.Components.Add(new ComponentRequirementItem
                {
                    ComponentId = "amd-hip-72",
                    FriendlyName = "AMD Compute Runtime (HIP)",
                    PlainEnglishPurpose = hipNote,
                    IsRequiredForSelectedProfile = profile.EnableNeuralRendering,
                    IsSatisfied = gpu.Hip72SdkAvailable && gpu.HipRuntimeInitialized,
                    CanDownloadAutomatically = false,
                    OfficialGuidanceUrl = "https://www.amd.com/en/developer/resources/rocm-hub/hip-sdk.html",
                    ActionPrompt = gpu.Hip72SdkAvailable && gpu.HipRuntimeInitialized ? "Detected & initialized on Windows" : "Install compatible AMD HIP runtime from AMD's official portal",
                    TechnicalDetail = gpu.Hip72SdkAvailable
                        ? $"Found {gpu.HipRuntimeDllName} ({gpu.HipRuntimeVersion}) · Init={(gpu.HipRuntimeInitialized ? "OK" : gpu.HipInitDiagnostic)}"
                        : $"Active probe: {(gpu.HipRuntimeInstalled ? $"{gpu.HipRuntimeDllName} v{gpu.HipRuntimeVersion} (Initialized={gpu.HipRuntimeInitialized})" : "None")} · Missing amdhip64_7.dll (official docs exclude gfx1032)"
                });
            }

            // Check imported Vault copy OR NeuralHub data\index.ini imported nvngx_dlssnr.dll against selected model
            var reqManifest = _dllVault.GetNeuralRequirementsManifest();
            var modelReq = reqManifest.GetModelRequirement(profile.SelectedNeuralModel);
            var validNr = existingNrVault;
            string effectiveVer = validNr?.FileVersion ?? (hubReport.IsDllImportedInNeuralHub ? hubReport.NeuralHubDllVersion : string.Empty);
            bool effectiveCc = (validNr?.SupportsCcModel == true) ||
                               (hubReport.IsDllImportedInNeuralHub && UserDllVault.ParseNormalizedVersion(hubReport.NeuralHubDllVersion) >= new Version(310, 9, 16, 0));
            bool effectiveHt = (validNr?.SupportsHtModel == true) ||
                               (UserDllVault.ParseNormalizedVersion(effectiveVer) >= new Version(310, 8, 0, 0));

            bool hasAnyNrDll = validNr != null || hubReport.IsDllImportedInNeuralHub;
            bool modelSatisfied = hasAnyNrDll && (profile.SelectedNeuralModel == NeuralRenderingModelType.CC ? effectiveCc : effectiveHt);
            string locationLabel = validNr != null && hubReport.IsVaultSynchronizedWithNeuralHub
                ? "NeuralHub & Vault synchronized"
                : (validNr != null ? "Project Luma Vault" : "NeuralHub DLL store");
            string modelNote = !hasAnyNrDll
                ? "Not found in NeuralHub store, DLL Vault, game libraries, or Downloads yet"
                : modelSatisfied
                    ? (effectiveCc
                        ? $"Verified v{effectiveVer} via {locationLabel} (CC & HT models supported)"
                        : $"Verified v{effectiveVer} via {locationLabel} (HT model ready; >= {reqManifest.GetModelRequirement(NeuralRenderingModelType.CC).MinimumVersion} needed for CC model)")
                    : $"Imported v{effectiveVer} does not meet {profile.SelectedNeuralModel} requirement (>= {modelReq.MinimumVersion})";

            plan.Components.Add(new ComponentRequirementItem
            {
                ComponentId = "nvngx_dlssnr",
                FriendlyName = "User-Owned Neural Rendering Model",
                PlainEnglishPurpose = "Proprietary runtime component sourced directly from an authorized installation or game folder on your PC.",
                IsRequiredForSelectedProfile = profile.EnableNeuralRendering,
                IsSatisfied = modelSatisfied,
                CanDownloadAutomatically = false,
                OfficialGuidanceUrl = reqManifest.OfficialReleaseUrl,
                ActionPrompt = modelSatisfied ? modelNote : "Use Find Required Components or Import NVIDIA DLL in Enhancement Library",
                TechnicalDetail = modelSatisfied ? modelNote : $"Requires legitimate local nvngx_dlssnr.dll (>= {modelReq.MinimumVersion} for {profile.SelectedNeuralModel} model)"
            });
        }

        plan.RequiresUserAttention = plan.Components.Any(c => c.IsRequiredForSelectedProfile && !c.IsSatisfied && !c.CanDownloadAutomatically);
        plan.ReadyToInstallImmediately = plan.Components.All(c => !c.IsRequiredForSelectedProfile || c.IsSatisfied || c.CanDownloadAutomatically);
        plan.SummaryBanner = plan.RequiresUserAttention
            ? "One or more AI Visuals prerequisites need your attention. Use Find Required Components to scan or import your NVIDIA DLL."
            : "All required enhancement components are ready or can be prepared automatically.";

        return plan;
    }

    public async Task<AiVisualsSetupResult> ExecuteAiVisualsDiscoveryAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        DetectedGpuInfo gpu,
        IEnumerable<string>? registeredGameDirectories = null,
        IProgress<SetupProgressStep>? progress = null,
        CancellationToken ct = default)
    {
        void Report(string key, string label, SetupStepState state, string detail = "")
            => progress?.Report(new SetupProgressStep { Key = key, Label = label, State = state, Detail = detail });

        var result = new AiVisualsSetupResult();
        var blockers = new List<string>();

        // 1. Checking GPU compatibility
        Report("gpu", "Checking GPU compatibility", SetupStepState.InProgress, $"{gpu.ModelName} ({gpu.GpuTarget})");
        ct.ThrowIfCancellationRequested();
        bool gpuSupported = gpu.Architecture >= GpuArchitecture.RDNA2 && (gpu.DriverVersion == "Unknown" || gpu.MeetsAdrenalin2611Minimum);
        if (!gpuSupported)
        {
            string msg = gpu.Architecture < GpuArchitecture.RDNA2
                ? $"{gpu.ModelName} ({gpu.Architecture}) is unsupported; RDNA 2 or newer is required."
                : $"Installed driver {gpu.DriverVersion} may predate Adrenalin 26.1.1.";
            blockers.Add(msg);
            Report("gpu", "Checking GPU compatibility", SetupStepState.AttentionRequired, msg);
        }
        else
        {
            Report("gpu", "Checking GPU compatibility", SetupStepState.Complete, $"{gpu.ModelName} ({gpu.Architecture} / {gpu.GpuTarget}) · Adrenalin {gpu.AdrenalinVersionDisplay}");
        }

        // 2. Checking game compatibility
        Report("game", "Checking game compatibility", SetupStepState.InProgress, game.Title);
        ct.ThrowIfCancellationRequested();
        bool gameOk = game.Is64Bit
                      && game.AntiCheatStatus is not (AntiCheatStatus.DetectedBlocked or AntiCheatStatus.WindowsAppsProtected)
                      && (game.GraphicsApi == GraphicsApi.DirectX12 || game.GraphicsApi == GraphicsApi.Unknown);
        if (!gameOk)
        {
            string msg = !game.Is64Bit
                ? "Requires 64-bit executable."
                : (game.AntiCheatStatus is AntiCheatStatus.DetectedBlocked or AntiCheatStatus.WindowsAppsProtected
                    ? $"Blocked by protection: {game.AntiCheatDetails}"
                    : $"Game uses {game.GraphicsApi}; Neural Rendering requires DirectX 12.");
            blockers.Add(msg);
            Report("game", "Checking game compatibility", SetupStepState.AttentionRequired, msg);
        }
        else
        {
            string inputs = (game.NativeDlssSupported || game.NativeFsrSupported || game.NativeXeSsSupported)
                ? "Compatible 64-bit DirectX 12 upscaling inputs detected"
                : "64-bit DirectX 12 executable verified";
            Report("game", "Checking game compatibility", SetupStepState.Complete, inputs);
        }

        // 3. Checking Neural Rendering backend
        Report("backend", "Checking Neural Rendering backend", SetupStepState.InProgress, "Inspecting DLSS-NR-on-AMD v0.9.0 / NeuralHub");
        ct.ThrowIfCancellationRequested();
        var manifests = await _packageManager.GetManifestsAsync(ct);
        var nrManifest = manifests.FirstOrDefault(m => m.Id.Equals("dlss-nr-on-amd", StringComparison.OrdinalIgnoreCase));
        var existingVaulted = (await _dllVault.GetImportedDllsAsync(ct))
            .FirstOrDefault(d => d.DllKind.Equals("nvngx_dlssnr", StringComparison.OrdinalIgnoreCase) && File.Exists(d.StoredPath));
        var hubReport = NeuralHubDetector.InspectFullStatus(nrManifest?.StagedInstallPath, existingVaulted, new[] { game }, _neuralHubProbeOptions);
        bool hubDetected = hubReport.IsAppDetected && hubReport.IsRuntimeVerified;
        result.NeuralHub = new NeuralHubDetectionResultInfo
        {
            IsDetected = hubDetected,
            InstallDirectory = hubReport.StorePath,
            ExecutablePath = hubReport.ExecutablePath,
            Version = string.IsNullOrWhiteSpace(hubReport.RuntimeVersion) ? hubReport.AppVersion : hubReport.RuntimeVersion,
            Summary = hubReport.StatusHeadline
        };
        if (hubDetected)
        {
            Report("backend", "Checking Neural Rendering backend", SetupStepState.Complete, hubReport.StatusHeadline);
        }
        else
        {
            blockers.Add("NeuralHub (DLSS-NR-on-AMD v0.9.0) not detected on this PC.");
            Report("backend", "Checking Neural Rendering backend", SetupStepState.AttentionRequired, "NeuralHub v0.9.0 not installed (acquire from official GitHub releases).");
        }

        // 4. Scanning game folders (bounded depth <= 3)
        Report("gamescan", "Scanning game folders", SetupStepState.InProgress, "Bounded depth <= 3 scan");
        ct.ThrowIfCancellationRequested();
        var gameDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(game.InstallDirectory)) gameDirs.Add(game.InstallDirectory);
        if (!string.IsNullOrWhiteSpace(game.ExecutableDirectory)) gameDirs.Add(game.ExecutableDirectory);
        if (registeredGameDirectories != null) gameDirs.AddRange(registeredGameDirectories);
        var gameCandidates = await _dllVault.DiscoverCandidateDllsAsync(gameDirs, "Installed Game", profile.SelectedNeuralModel, 3, ct);
        var nrGameCandidates = gameCandidates.Where(c => c.Validation.FileName.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)).ToList();
        result.DiscoveredCandidates.AddRange(nrGameCandidates);
        Report("gamescan", "Scanning game folders", SetupStepState.Complete,
            nrGameCandidates.Count > 0 ? $"Found {nrGameCandidates.Count} nvngx_dlssnr.dll candidate(s)" : "No nvngx_dlssnr.dll found in scanned game folders");

        // 5. Scanning Downloads (bounded depth <= 3)
        Report("dlscan", "Scanning Downloads", SetupStepState.InProgress, "Checking user Downloads folder");
        ct.ThrowIfCancellationRequested();
        string downloadsDir = !string.IsNullOrWhiteSpace(_defaultDownloadsDirectory)
            ? _defaultDownloadsDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dlCandidates = Directory.Exists(downloadsDir)
            ? await _dllVault.DiscoverCandidateDllsAsync(new[] { downloadsDir }, "Downloads", profile.SelectedNeuralModel, 3, ct)
            : Array.Empty<DiscoveredDllCandidate>();
        var nrDlCandidates = dlCandidates.Where(c => c.Validation.FileName.Equals("nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase)).ToList();
        result.DiscoveredCandidates.AddRange(nrDlCandidates);
        Report("dlscan", "Scanning Downloads", SetupStepState.Complete,
            nrDlCandidates.Count > 0 ? $"Found {nrDlCandidates.Count} nvngx_dlssnr.dll candidate(s) in Downloads" : "No nvngx_dlssnr.dll found in Downloads");

        // 6. Validating model DLL
        Report("dll", "Validating model DLL", SetupStepState.InProgress, $"Checking NeuralHub & Vault for nvngx_dlssnr.dll ({profile.SelectedNeuralModel} model)");
        ct.ThrowIfCancellationRequested();
        var vaultedReport = await _dllVault.VerifyImportedDllAsync("nvngx_dlssnr.dll", profile.SelectedNeuralModel, ct);
        bool nhDllSatisfied = hubReport.IsDllImportedInNeuralHub &&
            (profile.SelectedNeuralModel == NeuralRenderingModelType.CC
                ? UserDllVault.ParseNormalizedVersion(hubReport.NeuralHubDllVersion) >= new Version(310, 9, 16, 0)
                : UserDllVault.ParseNormalizedVersion(hubReport.NeuralHubDllVersion) >= new Version(310, 8, 0, 0));
        bool anyDllReady = vaultedReport.IsAcceptableForImport || nhDllSatisfied;

        if (vaultedReport.IsAcceptableForImport)
        {
            Report("dll", "Validating model DLL", SetupStepState.Complete,
                hubReport.IsVaultSynchronizedWithNeuralHub
                    ? $"{vaultedReport.Message} (Synchronized with NeuralHub)"
                    : vaultedReport.Message);
        }
        else if (nhDllSatisfied)
        {
            Report("dll", "Validating model DLL", SetupStepState.Complete,
                $"Verified inside NeuralHub store: nvngx_dlssnr.dll v{hubReport.NeuralHubDllVersion} ({hubReport.NeuralHubSupportedModels}) — no Vault duplication needed.");
        }
        else if (result.DiscoveredCandidates.Any(c => c.CanImport))
        {
            string msg = "Compatible nvngx_dlssnr.dll found on PC — click Use This File to authorize import into Vault.";
            blockers.Add(msg);
            Report("dll", "Validating model DLL", SetupStepState.AttentionRequired, msg);
        }
        else
        {
            string reqVer = _dllVault.GetNeuralRequirementsManifest().GetModelRequirement(profile.SelectedNeuralModel).MinimumVersion;
            string msg = vaultedReport.Status == DllValidationStatus.NotImported
                ? $"Missing user-owned nvngx_dlssnr.dll (>= {reqVer} for {profile.SelectedNeuralModel} model). Use Import NVIDIA DLL."
                : vaultedReport.Message;
            blockers.Add(msg);
            Report("dll", "Validating model DLL", SetupStepState.AttentionRequired, msg);
        }

        // 7. Checking runtime initialization
        Report("runtime", "Checking runtime initialization", SetupStepState.InProgress, "Verifying AMD HIP compute path");
        ct.ThrowIfCancellationRequested();
        bool runtimeOk = gpu.Architecture != GpuArchitecture.RDNA2 || (gpu.Hip72SdkAvailable && gpu.HipRuntimeInitialized);
        if (runtimeOk)
        {
            Report("runtime", "Checking runtime initialization", SetupStepState.Complete,
                gpu.Architecture == GpuArchitecture.RDNA2
                    ? $"Initialized {gpu.HipRuntimeDllName} (HIP API {gpu.HipRuntimeApiVersion})"
                    : $"Native D3D12 compute path ready on {gpu.Architecture}");
        }
        else if (gpu.HipRuntimeInstalled && !gpu.HipRuntimeInitialized)
        {
            string msg = string.IsNullOrWhiteSpace(gpu.HipInitDiagnostic)
                ? $"HIP runtime {gpu.HipRuntimeDllName} failed initialization on {gpu.GpuTarget}."
                : gpu.HipInitDiagnostic;
            blockers.Add(msg);
            Report("runtime", "Checking runtime initialization", SetupStepState.Failed, msg);
        }
        else
        {
            string msg = $"AMD HIP SDK 7.2 (amdhip64_7.dll) not detected (official AMD docs exclude {gpu.GpuTarget}; experimental {gpu.HipRuntimeDllName} v{gpu.HipRuntimeVersion} initialized={gpu.HipRuntimeInitialized}).";
            blockers.Add(msg);
            Report("runtime", "Checking runtime initialization", SetupStepState.AttentionRequired, msg);
        }

        result.Plan = await AnalyzeRequirementsAsync(game, profile, gpu, ct);
        result.IsReadyToEnable = blockers.Count == 0 && gpuSupported && gameOk && hubDetected && anyDllReady && runtimeOk;
        result.BlockerSummary = blockers.Count == 0
            ? "All AI Visuals prerequisites are verified and ready."
            : string.Join(" ", blockers);

        _logger.Info("ComponentResolver", $"AI Visuals component discovery finished for {game.Title}: Ready={result.IsReadyToEnable}", result.BlockerSummary);
        return result;
    }

    public async Task<bool> ExecuteGuidedSetupAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        DetectedGpuInfo gpu,
        IProgress<SetupProgressStep>? progress = null,
        CancellationToken ct = default)
    {
        void Report(string key, string label, SetupStepState state, string detail = "")
            => progress?.Report(new SetupProgressStep { Key = key, Label = label, State = state, Detail = detail });

        // Step 1: Checking graphics compatibility
        Report("compat", "Checking graphics compatibility", SetupStepState.InProgress, $"{gpu.ModelName} ({gpu.Architecture})");
        ct.ThrowIfCancellationRequested();
        var analysis = await AnalyzeRequirementsAsync(game, profile, gpu, ct);
        Report("compat", "Checking graphics compatibility", SetupStepState.Complete, $"{game.GraphicsApi} verified");

        // Step 2: Selecting recommended enhancements
        Report("select", "Selecting recommended enhancements", SetupStepState.InProgress);
        ct.ThrowIfCancellationRequested();
        if (analysis.RequiresUserAttention && profile.EnableNeuralRendering && profile.EnableSuperResolution)
        {
            // Never silently switch off profile.EnableNeuralRendering; deploy Super Resolution and record exact NR prerequisite warning.
            Report("select", "Selecting recommended enhancements", SetupStepState.Complete,
                $"{profile.SelectedPreset} profile (AI Visuals pending missing prerequisite; Super Resolution active)");
            _logger.Warn("ComponentResolver", $"AI Visuals deferred during setup for {game.Title}; missing prerequisite(s): {analysis.SummaryBanner}");
        }
        else if (analysis.RequiresUserAttention)
        {
            Report("select", "Selecting recommended enhancements", SetupStepState.AttentionRequired, analysis.SummaryBanner);
            return false;
        }
        else
        {
            Report("select", "Selecting recommended enhancements", SetupStepState.Complete, $"{profile.SelectedPreset} profile ready");
        }

        // Step 3 & 4: Preparing / Downloading required components
        var settings = await _repo.LoadSettingsAsync(ct);
        var manifests = (await _packageManager.GetManifestsAsync(ct)).ToList();
        var opti = manifests.FirstOrDefault(m => m.Id == "optiscaler");

        Report("prepare", "Preparing required components", SetupStepState.InProgress);
        if (opti != null && (string.IsNullOrWhiteSpace(opti.StagedInstallPath) || !Directory.Exists(opti.StagedInstallPath)))
        {
            Report("prepare", "Preparing required components", SetupStepState.Complete);
            Report("download", "Downloading enhancement files", SetupStepState.InProgress, "Fetching official OptiScaler release...");
            var rel = await _packageManager.CheckLatestReleaseAsync(opti, settings.UpdateChannel, ct);
            if (rel == null)
                throw new InvalidOperationException("Could not reach GitHub Releases to download required enhancement files. Check your internet connection or import a release archive in Available Enhancements.");

            await _packageManager.DownloadAndStageAsync(opti, rel, null, ct);
            manifests = (await _packageManager.GetManifestsAsync(ct)).ToList();
            Report("download", "Downloading enhancement files", SetupStepState.Complete, $"Verified {rel.TagName}");
        }
        else
        {
            Report("prepare", "Preparing required components", SetupStepState.Complete, "Using verified local components");
            Report("download", "Downloading enhancement files", SetupStepState.Complete, "Already cached locally");
        }

        // Step 5: Preparing game configuration
        Report("config", "Preparing game configuration", SetupStepState.InProgress);
        ct.ThrowIfCancellationRequested();
        var userDlls = await _dllVault.GetImportedDllsAsync(ct);
        var installPlan = _orchestrator.ComposeCombinedPlan(game, profile, manifests, userDlls);
        Report("config", "Preparing game configuration", SetupStepState.Complete, $"{installPlan.FilesToDeploy.Count} managed file(s)");

        // Step 6: Creating recovery point & applying
        Report("backup", "Creating a recovery point", SetupStepState.InProgress, "Backing up original game files...");
        await _installer.InstallOrUpdateAsync(game, profile, installPlan, ct);
        game.LastEnhancedUtc = DateTime.UtcNow;
        game.FriendlyStatus = FriendlyGameStatus.Enhanced;
        game.FriendlyStatusText = "Enhanced";
        game.RecommendationSummary = "Project Luma enhancements are active and verified on this game.";
        await _repo.SaveGameProfileAsync(profile, ct);
        Report("backup", "Creating a recovery point", SetupStepState.Complete, "Enhancements active & recoverable");

        _logger.Info("ComponentResolver", $"Completed guided setup for {game.Title}");
        return true;
    }

    public Task<UserImportedDllRecord> LocateComponentOnPcAsync(string filePath, CancellationToken ct = default)
        => _dllVault.ImportDllAsync(filePath, ct);

    public async Task<int> AutoDiscoverDownloadedComponentsAsync(
        string? downloadsDirectory = null,
        IEnumerable<string>? additionalGameDirs = null,
        CancellationToken ct = default)
    {
        int discoveredCount = 0;

        string resolvedDownloads = !string.IsNullOrWhiteSpace(downloadsDirectory)
            ? downloadsDirectory
            : (!string.IsNullOrWhiteSpace(_defaultDownloadsDirectory)
                ? _defaultDownloadsDirectory
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

        var scanRoots = new List<string>();
        if (additionalGameDirs != null)
            scanRoots.AddRange(additionalGameDirs);
        if (Directory.Exists(resolvedDownloads))
            scanRoots.Add(resolvedDownloads);

        if (scanRoots.Count > 0)
        {
            var foundDlls = await _dllVault.ScanGameLibrariesForUserDllsAsync(scanRoots, ct);
            discoveredCount += foundDlls.Count;
        }

        if (!Directory.Exists(resolvedDownloads))
            return discoveredCount;

        var manifests = await _packageManager.GetManifestsAsync(ct);
        var nrManifest = manifests.FirstOrDefault(m => m.Id == "dlss-nr-on-amd");
        if (nrManifest != null)
        {
            bool alreadyStaged = NeuralHubDetector.HasRuntimeBinaryPayload(nrManifest.StagedInstallPath);
            if (!alreadyStaged)
            {
                string? candidateArchive = await Task.Run(() => FindOfficialUserArchive(resolvedDownloads, "DLSS-NR-on-AMD"), ct);
                if (!string.IsNullOrWhiteSpace(candidateArchive))
                {
                    try
                    {
                        string versionHint = InferTagFromFileName(candidateArchive, nrManifest.PinnedExperimentalVersion);
                        var staged = await _packageManager.StageLocalArchiveAsync(nrManifest, candidateArchive, versionHint, ct);
                        if (!NeuralHubDetector.HasRuntimeBinaryPayload(staged.StagedInstallPath))
                        {
                            await _packageManager.RemoveBackendAsync(nrManifest.Id, ct);
                            _logger.Warn("ComponentResolver", $"Ignored source-only DLSS-NR-on-AMD archive lacking runtime binaries: {candidateArchive}");
                        }
                        else
                        {
                            discoveredCount++;
                            _logger.Info("ComponentResolver", $"Auto-discovered local DLSS-NR-on-AMD archive from Downloads: {candidateArchive}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("ComponentResolver", $"Skipped unusable archive in Downloads ({candidateArchive}): {ex.Message}");
                    }
                }
            }
        }

        return discoveredCount;
    }

    private static string? FindOfficialUserArchive(string directory, string keyword)
    {
        try
        {
            var candidates = Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, "*.7z", SearchOption.TopDirectoryOnly))
                .Where(f => Path.GetFileName(f).Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            if (candidates.Count > 0)
                return candidates[0];

            var extractedDir = Directory.EnumerateDirectories(directory)
                .Where(d => Path.GetFileName(d).Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                            Directory.EnumerateFiles(d, "*.dll", SearchOption.AllDirectories).Any())
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();

            return extractedDir;
        }
        catch
        {
            return null;
        }
    }

    private static string InferTagFromFileName(string path, string fallbackVersion)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"v?\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?");
        if (match.Success)
            return match.Value.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? match.Value : "v" + match.Value;
        return string.IsNullOrWhiteSpace(fallbackVersion) ? "v0.9.0" : fallbackVersion;
    }
}
