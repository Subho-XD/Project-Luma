using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;
using ProjectLuma.BackendManager;
using ProjectLuma.Compatibility;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using ProjectLuma.GameDiscovery;
using ProjectLuma.GameInstaller;
using ProjectLuma.Logging;
using ProjectLuma.PackageManager;
using ProjectLuma.UI.ViewModels;
using ProjectLuma.UpdateService;
using Xunit;

namespace ProjectLuma.Tests;

public sealed class RepairAndReleaseReadinessTests
{
    private static readonly string System64VersionDll = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "version.dll");

    private static readonly string System32WowVersionDll = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        "version.dll");

    private static string CreatePatchedPe64Dll(string targetPath, ushort major, ushort minor, ushort build, ushort rev, bool stripSignature = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        byte[] bytes = File.ReadAllBytes(System64VersionDll);
        // Locate VS_FIXEDFILEINFO signature 0xFEEF04BD (little-endian: BD 04 EF FE)
        int sigIdx = -1;
        for (int i = 0; i <= bytes.Length - 20; i++)
        {
            if (bytes[i] == 0xBD && bytes[i + 1] == 0x04 && bytes[i + 2] == 0xEF && bytes[i + 3] == 0xFE)
            {
                sigIdx = i;
                break;
            }
        }
        Assert.True(sigIdx >= 0, "Could not locate VS_FIXEDFILEINFO signature in version.dll test fixture.");
        uint ms = ((uint)major << 16) | minor;
        uint ls = ((uint)build << 16) | rev;
        BitConverter.GetBytes(ms).CopyTo(bytes, sigIdx + 8);
        BitConverter.GetBytes(ls).CopyTo(bytes, sigIdx + 12);

        if (stripSignature && bytes.Length > 0x40)
        {
            int peOffset = BitConverter.ToInt32(bytes, 0x3C);
            int certDirOffset = peOffset + 24 + 144; // PE32+ OptionalHeader Certificate Table DataDirectory[4]
            if (certDirOffset > 0 && certDirOffset + 8 <= bytes.Length)
                Array.Clear(bytes, certDirOffset, 8);
        }

        File.WriteAllBytes(targetPath, bytes);
        return targetPath;
    }

    private static DetectedGpuInfo CreateRx6600(bool hip72Available = false) => new()
    {
        ModelName = "AMD Radeon RX 6600",
        Architecture = GpuArchitecture.RDNA2,
        GpuTarget = "gfx1032",
        VramBytes = 8546959360UL,
        DriverVersion = "26.11.2",
        DirectX12Supported = true,
        VulkanSupported = true,
        HipRuntimeInstalled = true,
        HipRuntimeInitialized = true,
        HipRuntimeDllName = "amdhip64_6.dll",
        HipRuntimeVersion = "10.0.3652.0",
        HipRuntimeApiVersion = 60024000,
        Hip72SdkAvailable = hip72Available,
        Hip72OfficiallySupported = false
    };

    private static DiscoveredGame CreateSampleDx12Game(string dir, bool nativeFg = false, GraphicsApi api = GraphicsApi.DirectX12, bool is64Bit = true)
    {
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "SampleGame.exe");
        File.WriteAllText(exe, "MZ_DUMMY");
        return new DiscoveredGame
        {
            Id = "game-" + Path.GetFileName(dir),
            Title = "Test Title",
            ExecutablePath = exe,
            ExecutableDirectory = dir,
            InstallDirectory = dir,
            Launcher = GameLauncherType.Manual,
            GraphicsApi = api,
            EngineName = "Unreal Engine 5",
            Is64Bit = is64Bit,
            NativeDlssSupported = true,
            NativeFsrSupported = true,
            NativeFrameGenSupported = nativeFg
        };
    }

    [Fact]
    public void VersionParser_HandlesCommaSeparatedAndDottedNvidiaStrings()
    {
        Assert.Equal(new Version(310, 6, 0, 0), UserDllVault.ParseNormalizedVersion("310,6,0,0"));
        Assert.Equal(new Version(310, 9, 16, 0), UserDllVault.ParseNormalizedVersion("310, 9, 16, 0"));
        Assert.Equal(new Version(310, 8, 0, 0), UserDllVault.ParseNormalizedVersion("310.8.0.0-alpha"));
    }

    [Fact]
    public async Task UserDllVault_ValidatesPe64_Models_VaultStorage_Duplicates_Verify_Remove_And_DepthLimit()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaVaultTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();
            string vaultDir = Path.Combine(tempRoot, "UserComponents");
            var vault = new UserDllVault(repo, logger, vaultDir);

            // 1. Valid x64 DLL with 310.9.16.0 supports CC & HT and reports unsigned limitation clearly
            string ccDll = CreatePatchedPe64Dll(Path.Combine(tempRoot, "src_cc", "nvngx_dlssnr.dll"), 310, 9, 16, 0);
            var ccReport = await vault.ValidateDllAsync(ccDll, NeuralRenderingModelType.CC);
            Assert.True(ccReport.IsPeValid);
            Assert.True(ccReport.Is64Bit);
            Assert.True(ccReport.SupportsCcModel);
            Assert.True(ccReport.SupportsHtModel);
            Assert.True(ccReport.IsAcceptableForImport);
            Assert.False(string.IsNullOrWhiteSpace(ccReport.SignatureLimitationNote));
            Assert.Equal(DllValidationStatus.SignatureVerificationUnavailable, ccReport.Status);

            var imported = await vault.ImportDllAsync(ccDll, NeuralRenderingModelType.CC);
            Assert.Equal("310.9.16.0", imported.FileVersion);
            Assert.True(File.Exists(imported.StoredPath));
            Assert.True(File.Exists(imported.MetadataPath));
            Assert.Contains(Path.Combine("NVIDIA", "DLSS-NR", "310.9.16.0"), imported.StoredPath, StringComparison.OrdinalIgnoreCase);

            // Duplicate check with identical SHA-256 reuses single stored copy
            var duplicate = await vault.ImportDllAsync(ccDll, NeuralRenderingModelType.CC);
            Assert.Equal(imported.Sha256, duplicate.Sha256);
            Assert.Equal(imported.StoredPath, duplicate.StoredPath);
            Assert.Single(await vault.GetImportedDllsAsync());

            // Verify passes cleanly
            var verifyOk = await vault.VerifyImportedDllAsync("nvngx_dlssnr.dll", NeuralRenderingModelType.CC);
            Assert.True(verifyOk.IsAcceptableForImport);

            // Remove deletes vault files + metadata
            await vault.DeleteDllAsync("nvngx_dlssnr.dll");
            Assert.Empty(await vault.GetImportedDllsAsync());
            Assert.False(File.Exists(imported.StoredPath));

            // 2. Version 310.8.0.0 passes HT, fails CC as UnsupportedModel
            string htDll = CreatePatchedPe64Dll(Path.Combine(tempRoot, "src_ht", "nvngx_dlssnr.dll"), 310, 8, 0, 0);
            var htAsHt = await vault.ValidateDllAsync(htDll, NeuralRenderingModelType.HT);
            var htAsCc = await vault.ValidateDllAsync(htDll, NeuralRenderingModelType.CC);
            Assert.True(htAsHt.IsAcceptableForImport);
            Assert.True(htAsHt.SupportsHtModel);
            Assert.False(htAsCc.IsAcceptableForImport);
            Assert.Equal(DllValidationStatus.UnsupportedModel, htAsCc.Status);

            // 3. Version 310.6.0.0 rejected as VersionTooOld
            string oldDll = CreatePatchedPe64Dll(Path.Combine(tempRoot, "src_old", "nvngx_dlssnr.dll"), 310, 6, 0, 0);
            var oldReport = await vault.ValidateDllAsync(oldDll, NeuralRenderingModelType.HT);
            Assert.Equal(DllValidationStatus.VersionTooOld, oldReport.Status);

            // 4. 32-bit PE32 rejected as WrongArchitecture
            if (File.Exists(System32WowVersionDll))
            {
                string wowTarget = Path.Combine(tempRoot, "src_x86", "nvngx_dlssnr.dll");
                Directory.CreateDirectory(Path.GetDirectoryName(wowTarget)!);
                File.Copy(System32WowVersionDll, wowTarget, overwrite: true);
                var x86Report = await vault.ValidateDllAsync(wowTarget, NeuralRenderingModelType.CC);
                Assert.Equal(DllValidationStatus.WrongArchitecture, x86Report.Status);
                Assert.False(x86Report.Is64Bit);
            }

            // 5. Invalid non-PE & corrupted truncated PE
            string txtDll = Path.Combine(tempRoot, "src_invalid", "nvngx_dlssnr.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(txtDll)!);
            await File.WriteAllTextAsync(txtDll, new string('A', 256));
            var txtReport = await vault.ValidateDllAsync(txtDll, NeuralRenderingModelType.CC);
            Assert.Equal(DllValidationStatus.InvalidDll, txtReport.Status);

            string truncatedDll = Path.Combine(tempRoot, "src_trunc", "nvngx_dlssnr.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(truncatedDll)!);
            await File.WriteAllBytesAsync(truncatedDll, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
            var truncReport = await vault.ValidateDllAsync(truncatedDll, NeuralRenderingModelType.CC);
            Assert.Equal(DllValidationStatus.CorruptedFile, truncReport.Status);

            // 6. Bounded recursion (depth <= 3 finds candidate; depth >= 4 is ignored)
            string scanRoot = Path.Combine(tempRoot, "ScanRoot");
            string depth3Dir = Path.Combine(scanRoot, "a", "b", "c");
            string depth4Dir = Path.Combine(scanRoot, "a", "b", "c", "d");
            string candDepth3 = CreatePatchedPe64Dll(Path.Combine(depth3Dir, "nvngx_dlssnr.dll"), 310, 9, 16, 0);
            string candDepth4 = CreatePatchedPe64Dll(Path.Combine(depth4Dir, "nvngx_dlssnr.dll"), 310, 9, 17, 0);

            var candidates = await vault.DiscoverCandidateDllsAsync(new[] { scanRoot }, "Test Scan", NeuralRenderingModelType.CC, maxDepth: 3);
            Assert.Single(candidates);
            Assert.Equal(candDepth3, candidates[0].SourcePath);
            Assert.DoesNotContain(candidates, c => c.SourcePath == candDepth4);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task NeuralHubDetector_DistinguishesSourceDocsFromRuntimeBinaries()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaHubDetect_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string sourceOnly = Path.Combine(tempRoot, "docs_only");
            Directory.CreateDirectory(sourceOnly);
            await File.WriteAllTextAsync(Path.Combine(sourceOnly, "README.md"), "# DLSS-NR");
            await File.WriteAllTextAsync(Path.Combine(sourceOnly, "LICENSE"), "Personal non-commercial");
            await File.WriteAllTextAsync(Path.Combine(sourceOnly, "NeuralHub_v0.9.0_setup.exe"), "setup");
            Assert.False(NeuralHubDetector.HasRuntimeBinaryPayload(sourceOnly));

            string runtimeDir = Path.Combine(tempRoot, "runtime");
            Directory.CreateDirectory(runtimeDir);
            await File.WriteAllTextAsync(Path.Combine(runtimeDir, "NeuralHub.exe"), "MZ");
            Assert.True(NeuralHubDetector.HasRuntimeBinaryPayload(runtimeDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task AiVisualsDiscovery_Emits7Stages_FindsLocalDlls_And_NeverSilentlyResetsToggle()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaAiSetup_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();
            var pkgMgr = new GitHubPackageManager(repo, logger, stagingRootDirectory: Path.Combine(tempRoot, "backends"), neuralHubProbeOptions: NeuralHubProbeOptions.Disabled);
            var vault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));
            var orch = new BackendOrchestrator();
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "backups"));
            var emptyDl = Path.Combine(tempRoot, "empty_downloads");
            Directory.CreateDirectory(emptyDl);
            var resolver = new IntelligentComponentResolver(pkgMgr, vault, repo, orch, installer, logger, emptyDl, NeuralHubProbeOptions.Disabled);

            string gameDir = Path.Combine(tempRoot, "GameA");
            var game = CreateSampleDx12Game(gameDir);
            var gpu = CreateRx6600(hip72Available: false);
            var engine = new CapabilityEngine(NeuralHubProbeOptions.Disabled);
            var profile = engine.EvaluateGame(game, gpu, Array.Empty<UserImportedDllRecord>(), await pkgMgr.GetManifestsAsync(), null, UpdateChannel.Stable);
            profile.EnableNeuralRendering = true;

            // Place candidate inside game directory so discovery finds it
            CreatePatchedPe64Dll(Path.Combine(gameDir, "Binaries", "nvngx_dlssnr.dll"), 310, 9, 16, 0);

            var reportedSteps = new List<SetupProgressStep>();
            var progress = new SyncProgress<SetupProgressStep>(s => reportedSteps.Add(s));

            var result = await resolver.ExecuteAiVisualsDiscoveryAsync(game, profile, gpu, new[] { gameDir }, progress);
            Assert.True(profile.EnableNeuralRendering); // Never silently disabled
            Assert.Contains(reportedSteps, s => s.Label == "Checking GPU compatibility");
            Assert.Contains(reportedSteps, s => s.Label == "Checking game compatibility");
            Assert.Contains(reportedSteps, s => s.Label == "Checking Neural Rendering backend");
            Assert.Contains(reportedSteps, s => s.Label == "Scanning game folders");
            Assert.Contains(reportedSteps, s => s.Label == "Scanning Downloads");
            Assert.Contains(reportedSteps, s => s.Label == "Validating model DLL");
            Assert.Contains(reportedSteps, s => s.Label == "Checking runtime initialization");
            Assert.NotEmpty(result.DiscoveredCandidates);
            Assert.Contains(result.DiscoveredCandidates, c => c.CanImport);
            Assert.False(result.IsReadyToEnable); // RX 6600 missing amdhip64_7.dll + NeuralHub runtime
            Assert.False(string.IsNullOrWhiteSpace(result.BlockerSummary));

            // Test ViewModel FindRequiredComponentsCommand + UseDiscoveredNeuralCandidateCommand
            using var vm = CreateTestViewModel(repo, logger, gpu, pkgMgr, vault, resolver, installer, orch, new[] { game });
            await vm.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: false);
            vm.SelectedGame = vm.AllGames.First();
            await vm.RunAiVisualsSetupAsync();
            Assert.Equal("SETTING UP AI VISUAL ENHANCEMENT", vm.SetupDrawerHeader);
            Assert.True(vm.IsAiVisualsSetupDrawer);
            Assert.Equal(7, vm.SetupSteps.Count);
            Assert.NotEmpty(vm.DiscoveredSetupCandidates);

            var cand = vm.DiscoveredSetupCandidates.First(c => c.CanImport);
            Assert.True(vm.UseDiscoveredNeuralCandidateCommand.CanExecute(cand));
            vm.UseDiscoveredNeuralCandidateCommand.Execute(cand);
            for (int w = 0; w < 60 && !vm.HasImportedNeuralDll; w++)
                await Task.Delay(50);
            await Task.Delay(120); // allow trailing RefreshSelectedGameComponentsAsync SQLite read to close
            Assert.True(vm.HasImportedNeuralDll);
            Assert.Equal("310.9.16.0", vm.NeuralDetectedVersion);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public async Task FsrPresetPersistence_SurvivesRestartAndReevaluation_And_WritesOptiScalerIni()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaFsrPersist_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string dbPath = Path.Combine(tempRoot, "luma.db");
            string gameDir = Path.Combine(tempRoot, "RealLikeGame");
            var game = CreateSampleDx12Game(gameDir);
            var gpu = CreateRx6600();

            // Session 1: change FSR preset on GameCardViewModel and verify persisted to SQLite
            {
                var logger1 = new LumaLogger(Path.Combine(tempRoot, "logs"));
                var repo1 = new SqliteCompatibilityRepository(logger1, dbPath);
                await repo1.InitializeAsync();
                var pkgMgr1 = new GitHubPackageManager(repo1, logger1, stagingRootDirectory: Path.Combine(tempRoot, "backends"));
                var vault1 = new UserDllVault(repo1, logger1, Path.Combine(tempRoot, "vault"));
                var orch1 = new BackendOrchestrator();
                var inst1 = new TransactionalGameInstaller(new PeAnalyzer(), logger1, Path.Combine(tempRoot, "backups"));
                var res1 = new IntelligentComponentResolver(pkgMgr1, vault1, repo1, orch1, inst1, logger1);

                using var vm1 = CreateTestViewModel(repo1, logger1, gpu, pkgMgr1, vault1, res1, inst1, orch1, new[] { game });
                await vm1.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: false);

                var card1 = vm1.AllGames.Single();
                card1.SelectedUpscalingPreset = UpscalingQualityPreset.Performance;
                Assert.Equal(EnhancementProfilePreset.Custom, card1.Profile.SelectedPreset);
                Assert.Equal(UpscalingQualityPreset.Performance, card1.SelectedUpscalingPreset);

                // Reevaluate capabilities should NOT reset user's Performance choice
                vm1.ReevaluateAllCapabilities();
                Assert.Equal(UpscalingQualityPreset.Performance, card1.SelectedUpscalingPreset);

                // Allow async CommitSettingEdit SQLite save to complete
                await Task.Delay(250);
            }

            // Session 2 (simulated restart with fresh repository + ViewModel): Performance must load cleanly!
            var logger2 = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo2 = new SqliteCompatibilityRepository(logger2, dbPath);
            await repo2.InitializeAsync();
            var loaded = await repo2.LoadGameProfileAsync(game.Id);
            Assert.NotNull(loaded);
            Assert.Equal(UpscalingQualityPreset.Performance, loaded!.SelectedUpscalingPreset);
            Assert.Equal(EnhancementProfilePreset.Custom, loaded.SelectedPreset);

            var pkgMgr2 = new GitHubPackageManager(repo2, logger2, stagingRootDirectory: Path.Combine(tempRoot, "backends"));
            var vault2 = new UserDllVault(repo2, logger2, Path.Combine(tempRoot, "vault"));
            var orch2 = new BackendOrchestrator();
            var inst2 = new TransactionalGameInstaller(new PeAnalyzer(), logger2, Path.Combine(tempRoot, "backups"));
            var res2 = new IntelligentComponentResolver(pkgMgr2, vault2, repo2, orch2, inst2, logger2);

            using var vm2 = CreateTestViewModel(repo2, logger2, gpu, pkgMgr2, vault2, res2, inst2, orch2, new[] { game });
            await vm2.InitializeAsync(autoScan: false, showOnboardingIfFirstRun: false);
            var card2 = vm2.AllGames.Single();
            Assert.Equal(UpscalingQualityPreset.Performance, card2.SelectedUpscalingPreset);

            // Verify OptiScaler.ini generation writes exact Fsr4Preset & QualityOverrides for Balanced vs Performance
            string optiStage = Path.Combine(tempRoot, "optiStage");
            Directory.CreateDirectory(optiStage);
            await File.WriteAllTextAsync(Path.Combine(optiStage, "OptiScaler.dll"), "DUMMY_DLL");

            card2.Profile.SelectedUpscalerEngine = "AMD FSR 4.1.2 (INT8 RDNA2)";
            card2.Profile.SelectedUpscalingPreset = UpscalingQualityPreset.Performance;
            var adapter = new OptiScalerSuperResolutionAdapter();
            var planPerf = adapter.BuildInstallPlan(game, card2.Profile, optiStage, Array.Empty<UserImportedDllRecord>());
            var iniPerf = planPerf.FilesToDeploy.Single(f => f.IsIniMerge).IniSections;
            Assert.Equal("3", iniPerf["FSR"]["Fsr4Preset"]);
            Assert.Equal("2.0", iniPerf["QualityOverrides"]["QualityRatioPerformance"]);
            Assert.Equal("Performance", iniPerf["ProjectLuma"]["QualityPreset"]);

            card2.Profile.SelectedUpscalingPreset = UpscalingQualityPreset.Balanced;
            var planBal = adapter.BuildInstallPlan(game, card2.Profile, optiStage, Array.Empty<UserImportedDllRecord>());
            var iniBal = planBal.FilesToDeploy.Single(f => f.IsIniMerge).IniSections;
            Assert.Equal("2", iniBal["FSR"]["Fsr4Preset"]);
            Assert.Equal("1.7", iniBal["QualityOverrides"]["QualityRatioBalanced"]);
            Assert.Equal("Balanced", iniBal["ProjectLuma"]["QualityPreset"]);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    [Fact]
    public void GameplaySmoothness_EnforcesNativeFgGuide_OptiFgReady_Rx6000DlssFgBlock_And_Dx11VulkanAfmf()
    {
        var engine = new CapabilityEngine();
        var gpu = CreateRx6600();

        // Case A: Native Frame Generation present
        var nativeFgGame = CreateSampleDx12Game(Path.Combine(Path.GetTempPath(), "DummyFgA"), nativeFg: true);
        var pNative = engine.EvaluateGame(nativeFgGame, gpu, Array.Empty<UserImportedDllRecord>(), Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);
        var cardNative = new GameCardViewModel(nativeFgGame, pNative);
        Assert.Equal("Already available in this game", pNative.SmootherGameplayStatusText);
        Assert.Equal(CapabilityState.SupportedWithLimitations, cardNative.FrameGenBadgeState);
        Assert.False(cardNative.CanToggleFrameGen);
        Assert.False(pNative.EnableFrameGeneration);
        Assert.Contains("built-in Frame Generation", cardNative.FrameGenUnavailableReason, StringComparison.OrdinalIgnoreCase);

        // Case B: DX12 64-bit without native FG -> toggleable and persists
        var dx12NoNative = CreateSampleDx12Game(Path.Combine(Path.GetTempPath(), "DummyFgB"), nativeFg: false, GraphicsApi.DirectX12, is64Bit: true);
        var pDx12 = engine.EvaluateGame(dx12NoNative, gpu, Array.Empty<UserImportedDllRecord>(), Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);
        var cardDx12 = new GameCardViewModel(dx12NoNative, pDx12);
        Assert.True(cardDx12.CanToggleFrameGen);
        Assert.Equal(CapabilityState.Supported, cardDx12.FrameGenBadgeState);
        cardDx12.EnableFrameGeneration = true;
        Assert.True(cardDx12.EnableFrameGeneration);
        Assert.Equal("Included in your profile", pDx12.SmootherGameplayStatusText);

        // Case C1: Selecting DLSS-FG on RX 6000 blocks cleanly
        cardDx12.SelectedFrameGenEngine = "DLSS Frame Generation (DLSS-NR-on-AMD) [Unsupported]";
        Assert.False(cardDx12.CanToggleFrameGen);
        Assert.False(cardDx12.EnableFrameGeneration);
        Assert.Equal("Unavailable on RX 6000", pDx12.SmootherGameplayStatusText);
        Assert.Equal(CapabilityState.Unsupported, cardDx12.FrameGenBadgeState);

        // Case C2: Vulkan / DX11 game without native FG guides to AFMF 2
        var vkGame = CreateSampleDx12Game(Path.Combine(Path.GetTempPath(), "DummyFgVk"), nativeFg: false, GraphicsApi.Vulkan, is64Bit: true);
        var pVk = engine.EvaluateGame(vkGame, gpu, Array.Empty<UserImportedDllRecord>(), Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);
        var cardVk = new GameCardViewModel(vkGame, pVk);
        Assert.False(cardVk.CanToggleFrameGen);
        Assert.Equal("Use AMD Adrenalin AFMF 2", pVk.SmootherGameplayStatusText);
        Assert.Contains("AFMF", cardVk.FrameGenUnavailableReason, StringComparison.OrdinalIgnoreCase);

        // Case C3: 32-bit game -> Not supported
        var x86Game = CreateSampleDx12Game(Path.Combine(Path.GetTempPath(), "DummyFgx86"), nativeFg: false, GraphicsApi.DirectX12, is64Bit: false);
        var p32 = engine.EvaluateGame(x86Game, gpu, Array.Empty<UserImportedDllRecord>(), Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);
        var card32 = new GameCardViewModel(x86Game, p32);
        Assert.False(card32.CanToggleFrameGen);
        Assert.Equal("Not supported", p32.SmootherGameplayStatusText);
    }

    [Fact]
    public void TrayLifecycleController_HandlesHideRestoreExplorerRestart_And_ExplicitExit()
    {
        var controller = new TrayLifecycleController();
        int hides = 0, shows = 0, exits = 0, recreates = 0;
        controller.HideRequested += () => hides++;
        controller.ShowRequested += () => shows++;
        controller.ExitRequested += () => exits++;
        controller.RecreateTrayIconRequested += () => recreates++;

        // Close when minimize-to-tray disabled -> exit cleanly (return false)
        Assert.False(controller.HandleWindowClosing(minimizeToTrayEnabled: false));
        Assert.False(controller.IsWindowHiddenToTray);

        // Enable minimize-to-tray -> close cancels shutdown & hides to tray
        controller.SyncPreference(true);
        Assert.True(controller.TrayIconVisible);
        Assert.True(controller.HandleWindowClosing(minimizeToTrayEnabled: true));
        Assert.True(controller.IsWindowHiddenToTray);
        Assert.Equal(1, hides);

        // Explorer restart (TaskbarCreated) recreates icon
        controller.HandleTaskbarCreated();
        Assert.Equal(1, controller.ExplorerRestartRecoveryCount);
        Assert.Equal(1, recreates);

        // Restore brings window back
        controller.RequestRestore();
        Assert.False(controller.IsWindowHiddenToTray);
        Assert.Equal(1, shows);

        // Explicit exit bypasses tray hide
        controller.RequestExplicitExit();
        Assert.True(controller.IsExplicitExitRequested);
        Assert.False(controller.TrayIconVisible);
        Assert.Equal(1, exits);
        Assert.False(controller.HandleWindowClosing(minimizeToTrayEnabled: true));
    }

    [Fact]
    public void WindowsStartupService_RegistersReadsUpdatesAndDisablesHkcuRunEntry()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaStartup_" + Guid.NewGuid().ToString("N")[..8]);
        string testSubKey = $@"Software\ProjectLumaTest\{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(tempRoot);
            string exeV1 = Path.Combine(tempRoot, "v1", "ProjectLuma.exe");
            string exeV2 = Path.Combine(tempRoot, "v2", "ProjectLuma.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exeV1)!);
            Directory.CreateDirectory(Path.GetDirectoryName(exeV2)!);
            File.WriteAllText(exeV1, "MZ_V1");
            File.WriteAllText(exeV2, "MZ_V2");

            string fakeStartupFolder = Path.Combine(tempRoot, "StartupFolder");
            Directory.CreateDirectory(fakeStartupFolder);
            string duplicateShortcut = Path.Combine(fakeStartupFolder, "Project Luma.lnk");
            File.WriteAllText(duplicateShortcut, "LNK");

            var startup = new WindowsStartupService(testSubKey, "ProjectLumaTestApp", fakeStartupFolder);
            Assert.False(startup.IsEnabled());

            startup.Enable(exeV1);
            Assert.True(startup.IsEnabled());
            Assert.Equal($"\"{exeV1}\"", startup.GetRegisteredCommand());
            Assert.False(File.Exists(duplicateShortcut)); // Cleaned duplicate shortcut

            startup.UpdateExecutablePathIfEnabled(exeV2);
            Assert.Equal($"\"{exeV2}\"", startup.GetRegisteredCommand());

            startup.Disable();
            Assert.False(startup.IsEnabled());
            Assert.Null(startup.GetRegisteredCommand());
        }
        finally
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(testSubKey, throwOnMissingSubKey: false); } catch { }
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task DownloadsUi_ApplyUpdateItem_NonBlockingOnStaDispatcher_And_HandlesTimeoutAndCancel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaStaDl_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();
            var gate = new ManualResetEventSlim(false);
            var slowHandler = new DelayedDownloadHandler(gate);
            var pkgMgr = new GitHubPackageManager(repo, logger, new HttpClient(slowHandler) { Timeout = TimeSpan.FromMinutes(2) }, Path.Combine(tempRoot, "backends"));
            var vault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));
            var orch = new BackendOrchestrator();
            var inst = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "backups"));
            var resolver = new IntelligentComponentResolver(pkgMgr, vault, repo, orch, inst, logger);
            var gpu = CreateRx6600();

            Exception? threadError = null;
            int uiTimerTicks = 0;

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var staThread = new Thread(() =>
            {
                var disp = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(disp));

                async Task RunStaBodyAsync()
                {
                    try
                    {
                        using var vm = CreateTestViewModel(repo, logger, gpu, pkgMgr, vault, resolver, inst, orch, Array.Empty<DiscoveredGame>());
                        await vm.InitializeAsync(autoScan: false, showOnboardingIfFirstRun: false);

                        var updateItem = new UpdateCheckItem
                        {
                            ComponentId = "optiscaler",
                            ComponentName = "OptiScaler",
                            Kind = "Backend",
                            StateBadgeText = "Ready to install",
                            Status = "Official package ready",
                            ActionLabel = "Download Official",
                            CanApply = true
                        };
                        vm.UpdateItems.Add(updateItem);

                        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) => uiTimerTicks++, disp);
                        timer.Start();

                        vm.ApplyUpdateItemCommand.Execute(updateItem);

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        while ((!updateItem.IsBusy || !slowHandler.StreamReached) && sw.Elapsed < TimeSpan.FromSeconds(5))
                            await Task.Delay(15);
                        Assert.True(updateItem.IsBusy);

                        // Pump UI events while download is blocked — UI thread MUST stay fluid
                        int ticksBefore = uiTimerTicks;
                        for (int i = 0; i < 8; i++)
                            await Task.Delay(25);
                        Assert.True(uiTimerTicks > ticksBefore, "UI Dispatcher froze while Download Official was active.");

                        // Cancel cleanly without hang or unhandled exception
                        vm.CancelUpdateItemCommand.Execute(updateItem);
                        gate.Set();

                        sw.Restart();
                        while (updateItem.IsBusy && sw.Elapsed < TimeSpan.FromSeconds(5))
                            await Task.Delay(15);
                        Assert.False(updateItem.IsBusy);
                        timer.Stop();
                        done.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        threadError = ex;
                        done.TrySetException(ex);
                    }
                    finally
                    {
                        disp.BeginInvokeShutdown(DispatcherPriority.Normal);
                    }
                }

                _ = disp.BeginInvoke(new Action(() => _ = RunStaBodyAsync()));

                try
                {
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    threadError ??= ex;
                    done.TrySetException(ex);
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            await done.Task;
            Assert.Null(threadError);
        }
        finally
        {
            CleanupTempDir(tempRoot);
        }
    }

    private static void CleanupTempDir(string path)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(100);
            }
            catch
            {
                return;
            }
        }
    }

    private static void PumpFrame(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static MainViewModel CreateTestViewModel(
        ICompatibilityRepository repo,
        ILumaLogger logger,
        DetectedGpuInfo gpu,
        IPackageManager pkgMgr,
        IUserDllVault vault,
        IIntelligentComponentResolver resolver,
        IGameInstaller installer,
        IBackendOrchestrator orchestrator,
        IReadOnlyList<DiscoveredGame> games)
    {
        var gpuDetector = new StubGpuDetector(gpu);
        var capEngine = new CapabilityEngine();
        var scanner = new StubLibraryScanner(games);
        var art = new ArtworkDiscoveryService(logger, cacheDirectory: Path.Combine(Path.GetDirectoryName(((LumaLogger)logger).LogDirectory)!, "art"));
        var launch = new GameLaunchService(installer, logger);
        var update = new UpdateCoordinatorService(gpuDetector, repo, pkgMgr, orchestrator, installer, logger);
        return new MainViewModel(
            gpuDetector, capEngine, repo, scanner, art, pkgMgr, vault, orchestrator,
            resolver, installer, launch, update, logger, new StubDialogService());
    }

    private sealed class StubGpuDetector(DetectedGpuInfo gpu) : IGpuDetector
    {
        public Task<DetectedGpuInfo> DetectPrimaryGpuAsync(CancellationToken ct = default) => Task.FromResult(gpu);
        public IReadOnlyList<GpuCatalogEntry> GetCatalogEntries() => Array.Empty<GpuCatalogEntry>();
    }

    private sealed class StubLibraryScanner(IReadOnlyList<DiscoveredGame> games) : IGameLibraryScanner
    {
        public Task<IReadOnlyList<DiscoveredGame>> ScanAllAsync(IEnumerable<string> customPaths, IProgress<GameLibraryScanProgress>? progress = null, CancellationToken ct = default)
            => Task.FromResult(games);

        public Task<DiscoveredGame?> InspectSingleDirectoryOrExeAsync(string path, GameLauncherType launcher = GameLauncherType.Manual, string storeAppId = "", CancellationToken ct = default)
            => Task.FromResult<DiscoveredGame?>(null);
    }

    private sealed class StubDialogService : IUserDialogService
    {
        public string? PickFolder(string title) => null;
        public string? PickFile(string title, string filter) => null;
        public string? SaveFile(string title, string defaultFileName, string filter) => null;
        public void OpenUrl(string url) { }
        public void OpenFolder(string path) { }
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private sealed class DelayedDownloadHandler(ManualResetEventSlim gate) : HttpMessageHandler
    {
        public volatile bool StreamReached;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("api.github.com/repos/OptiScaler/OptiScaler/releases", StringComparison.OrdinalIgnoreCase))
            {
                string json = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        tag_name = "v0.9.5",
                        name = "OptiScaler v0.9.5",
                        prerelease = false,
                        published_at = DateTime.UtcNow,
                        html_url = "https://github.com/OptiScaler/OptiScaler/releases/tag/v0.9.5",
                        body = "Release",
                        assets = new[]
                        {
                            new
                            {
                                name = "OptiScaler_v0.9.5.7z",
                                browser_download_url = "https://github.com/OptiScaler/OptiScaler/releases/download/v0.9.5/OptiScaler_v0.9.5.7z",
                                size = 1024L,
                                digest = ""
                            }
                        }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new GatedStreamContent(() => StreamReached = true, gate)
            });
        }

        private sealed class GatedStreamContent(Action markEntered, ManualResetEventSlim gate) : HttpContent
        {
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                markEntered();
                byte[] chunk = new byte[512];
                await stream.WriteAsync(chunk);
                await stream.FlushAsync();
                gate.Wait(TimeSpan.FromSeconds(10));
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 1024;
                return true;
            }
        }
    }
}
