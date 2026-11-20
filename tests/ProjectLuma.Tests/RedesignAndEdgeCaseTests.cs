using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ProjectLuma.BackendManager;
using ProjectLuma.Compatibility;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using ProjectLuma.GameDiscovery;
using ProjectLuma.GameInstaller;
using ProjectLuma.GpuDetection;
using ProjectLuma.Logging;
using ProjectLuma.PackageManager;
using ProjectLuma.Setup;
using ProjectLuma.UI.Converters;
using ProjectLuma.UI.ViewModels;
using ProjectLuma.UI.Views;
using ProjectLuma.UpdateService;
using Xunit;
using Xunit.Abstractions;

namespace ProjectLuma.Tests;

public sealed class RedesignAndEdgeCaseTests
{
    private readonly ITestOutputHelper _output;

    public RedesignAndEdgeCaseTests(ITestOutputHelper output) => _output = output;

    private static DetectedGpuInfo CreateRx6600Gpu(bool outdatedDriver = false, bool hip72 = false) => new()
    {
        Manufacturer = "AMD",
        ModelName = "AMD Radeon RX 6600",
        VendorId = 0x1002,
        DeviceId = 0x73FF,
        Architecture = GpuArchitecture.RDNA2,
        GpuTarget = "gfx1032",
        VramBytes = 8541929472UL,
        DriverVersion = outdatedDriver ? "31.0.10000.1" : "32.0.21045.29003",
        AdrenalinVersionDisplay = outdatedDriver ? "24.1.1" : "26.11.2",
        MeetsAdrenalin2611Minimum = !outdatedDriver,
        DirectX12Supported = true,
        VulkanSupported = true,
        HipRuntimeInstalled = true,
        HipRuntimeDllName = hip72 ? "amdhip64_7.dll" : "amdhip64_6.dll",
        Hip72SdkAvailable = hip72
    };

    [Fact]
    public void MultiGpu_SelectBestGpuAdapter_PrioritizesDiscreteAmd()
    {
        var db = new AmdGpuArchitectureDatabase();
        var adapters = new List<DxgiGpuEnumerator.DxgiAdapterRecord>
        {
            new("Intel(R) UHD Graphics 770", 0x8086, 0x4680, 0, 0, 134_217_728UL, 8_000_000_000UL, 0),
            new("NVIDIA GeForce RTX 3060", 0x10DE, 0x2504, 0, 0, 12_884_901_888UL, 8_000_000_000UL, 0),
            new("AMD Radeon RX 6600", 0x1002, 0x73FF, 0, 0, 8_541_929_472UL, 8_000_000_000UL, 0)
        };

        var picked = WindowsGpuDetector.SelectBestGpuAdapter(adapters, db);
        Assert.NotNull(picked);
        Assert.Equal(0x1002u, picked!.VendorId);
        Assert.Equal(0x73FFu, picked.DeviceId);

        // Also test unknown AMD device ID classification falls back without exception
        var (unknownArch, unknownEntry) = db.Classify(0x1002, 0x9999);
        Assert.Equal(GpuArchitecture.Unknown, unknownArch);
        Assert.Null(unknownEntry);
    }

    [Fact]
    public void OutdatedDriverAndMissingHip_ReportsHonestDependencyStates()
    {
        var engine = new CapabilityEngine();
        var gpu = CreateRx6600Gpu(outdatedDriver: true, hip72: false);
        var game = new DiscoveredGame
        {
            Id = "test-dx12",
            Title = "Test DX12 Title",
            GraphicsApi = GraphicsApi.DirectX12,
            Is64Bit = true,
            NativeDlssSupported = true,
            NativeFsrSupported = true
        };

        var profile = engine.EvaluateGame(
            game, gpu, Array.Empty<UserImportedDllRecord>(),
            Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);

        Assert.Equal(CapabilityState.RequiresDriver, profile.NeuralRenderingState);
        Assert.Equal(CapabilityState.Supported, profile.FrameGenerationState);
        Assert.Equal(CapabilityState.Unsupported, profile.RayReconstructionState);

        var currentDriverProfile = engine.EvaluateGame(
            game, CreateRx6600Gpu(outdatedDriver: false, hip72: false), Array.Empty<UserImportedDllRecord>(),
            Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);
        Assert.Equal(CapabilityState.RequiresDependency, currentDriverProfile.NeuralRenderingState);
    }

    [Fact]
    public async Task GameScanner_ResilientToCorruptedManifestsAndMissingExes()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaEdgeScan_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string steamApps = Path.Combine(tempRoot, "steam", "steamapps");
            string validGameDir = Path.Combine(steamApps, "common", "Valid Game");
            string missingExeDir = Path.Combine(steamApps, "common", "MissingExeGame");
            Directory.CreateDirectory(validGameDir);
            Directory.CreateDirectory(missingExeDir);

            string sampleExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            File.Copy(sampleExe, Path.Combine(validGameDir, "ValidGame.exe"));

            File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"), $$"""
                "libraryfolders"
                {
                    "0" { "path" "{{Path.Combine(tempRoot, "steam").Replace("\\", "\\\\")}}" }
                }
                """);

            File.WriteAllText(Path.Combine(steamApps, "appmanifest_1001.acf"), """
                "AppState"
                {
                    "appid" "1001"
                    "name" "Valid Game"
                    "installdir" "Valid Game"
                }
                """);

            // Corrupted ACF manifest & missing-exe manifest must not crash scanner
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_9999.acf"), "{ broken acf syntax \0\0\0 \"\"\"");
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_1002.acf"), """
                "AppState"
                {
                    "appid" "1002"
                    "name" "Missing Exe Game"
                    "installdir" "MissingExeGame"
                }
                """);

            // Malformed Epic manifest JSON
            string epicDir = Path.Combine(tempRoot, "epic");
            Directory.CreateDirectory(epicDir);
            File.WriteAllText(Path.Combine(epicDir, "corrupt.item"), "{ not valid json !!!");

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var scanner = new MultiStoreGameScanner(logger, new PeAnalyzer());
            var found = await scanner.ScanWithOptionsAsync(
                Array.Empty<string>(),
                new MultiStoreGameScanner.ScanOptions
                {
                    SteamRootOverride = Path.Combine(tempRoot, "steam"),
                    EpicManifestsOverride = epicDir,
                    DisableRegistry = true
                });

            Assert.Single(found);
            Assert.Equal("Valid Game", found[0].Title);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task PeAndArtwork_HandlesUnicodeAndLongPathsSafely()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaUnicode_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            string gameDir = Path.Combine(tempRoot, "Pokémon 日本語 Edition — Extended Subfolder Name With Spaces");
            Directory.CreateDirectory(gameDir);
            string exePath = Path.Combine(gameDir, "UnicodeGame.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), exePath);

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var game = new DiscoveredGame
            {
                Id = "unicode-long-test",
                Title = "Pokémon 日本語 Edition",
                ExecutablePath = exePath,
                ExecutableDirectory = gameDir,
                InstallDirectory = gameDir,
                Launcher = GameLauncherType.Manual
            };

            var pe = new PeAnalyzer().InspectFile(exePath);
            Assert.NotNull(pe);
            Assert.True(pe!.Is64Bit);

            var artwork = new ArtworkDiscoveryService(logger, cacheDirectory: Path.Combine(tempRoot, "art"), credFilePath: Path.Combine(tempRoot, "sgdb.dpapi"));
            var artSet = await artwork.ResolveArtworkAsync(game, allowRemoteLookup: false);

            Assert.True(File.Exists(artSet.CoverImagePath));
            Assert.True(File.Exists(artSet.HeroImagePath));
            byte[] header = await File.ReadAllBytesAsync(artSet.CoverImagePath);
            Assert.True(header.Length > 64);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, header[..4]); // PNG magic signature
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task ArtworkDiscovery_LocalFolderOverrideAndDpapiRoundTripAndCacheClear()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaArtTest_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            string gameDir = Path.Combine(tempRoot, "LocalArtGame");
            string cacheDir = Path.Combine(tempRoot, "art_cache");
            string credFile = Path.Combine(tempRoot, "sgdb.dpapi");
            Directory.CreateDirectory(gameDir);

            byte[] minimalPng = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            string coverFile = Path.Combine(gameDir, "cover.png");
            string heroFile = Path.Combine(gameDir, "hero.png");
            await File.WriteAllBytesAsync(coverFile, minimalPng);
            await File.WriteAllBytesAsync(heroFile, minimalPng);

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var svc = new ArtworkDiscoveryService(logger, cacheDirectory: cacheDir, credFilePath: credFile);

            var game = new DiscoveredGame
            {
                Id = "local-art-game",
                Title = "Local Art Game",
                ExecutablePath = Path.Combine(gameDir, "game.exe"),
                ExecutableDirectory = gameDir,
                InstallDirectory = gameDir
            };

            var resolved = await svc.ResolveArtworkAsync(game, allowRemoteLookup: false);
            Assert.Equal(ArtworkSourceKind.LauncherMetadata, resolved.Source);
            Assert.Equal(coverFile, game.CoverImagePath);
            Assert.Equal(heroFile, game.HeroImagePath);

            // DPAPI API key protection & cleanup check
            Assert.False(svc.HasSteamGridDbApiKey());
            svc.SaveSteamGridDbApiKey("test-sgdb-key-12345");
            Assert.True(svc.HasSteamGridDbApiKey());
            Assert.True(File.Exists(credFile));
            Assert.DoesNotContain("test-sgdb-key-12345", await File.ReadAllTextAsync(credFile));
            svc.SaveSteamGridDbApiKey(null);
            Assert.False(svc.HasSteamGridDbApiKey());

            // Cache clear recreates empty cache dir
            svc.ClearCache();
            Assert.True(Directory.Exists(cacheDir));
            Assert.Empty(Directory.GetFiles(cacheDir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task ArtworkDiscovery_SteamLocalCache_ResolvesRealCacheIfPresent()
    {
        string steamCache4126040 = @"C:\Program Files (x86)\Steam\appcache\librarycache\4126040";
        if (!Directory.Exists(steamCache4126040)) return; // Skip cleanly if Steam isn't installed on host

        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaSteamCache_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var svc = new ArtworkDiscoveryService(
                new LumaLogger(Path.Combine(tempRoot, "logs")),
                cacheDirectory: Path.Combine(tempRoot, "cache"),
                credFilePath: Path.Combine(tempRoot, "cred.dpapi"));

            var game = new DiscoveredGame
            {
                Id = "steam-4126040",
                Title = "Aniimo",
                Launcher = GameLauncherType.Steam,
                StoreAppId = "4126040",
                InstallDirectory = Path.Combine(tempRoot, "missing"),
                ExecutableDirectory = Path.Combine(tempRoot, "missing")
            };

            var art = await svc.ResolveArtworkAsync(game, allowRemoteLookup: false);
            Assert.Equal(ArtworkSourceKind.SteamLocalCache, art.Source);
            Assert.True(File.Exists(art.CoverImagePath));
            Assert.True(File.Exists(art.HeroImagePath));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task CapabilityEngine_PresetsAndDoubleFgGuardAndAntiCheatWork()
    {
        var engine = new CapabilityEngine();
        var gpu = CreateRx6600Gpu(outdatedDriver: false, hip72: false);
        var gameWithNativeFg = new DiscoveredGame
        {
            Id = "fg-guard",
            Title = "Game With Built-in FG",
            GraphicsApi = GraphicsApi.DirectX12,
            Is64Bit = true,
            NativeDlssSupported = true,
            NativeFsrSupported = true,
            NativeFrameGenSupported = true
        };

        var profile = engine.EvaluateGame(
            gameWithNativeFg, gpu, Array.Empty<UserImportedDllRecord>(),
            Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);

        Assert.True(profile.DoubleFgGuardTriggered);
        Assert.False(profile.EnableFrameGeneration);
        Assert.Equal("Already available in this game", profile.SmootherGameplayStatusText);

        // Switch presets and verify Balanced -> VisualQuality -> SmootherGameplay -> Custom
        CapabilityEngine.ApplyPreset(profile, EnhancementProfilePreset.VisualQuality, gameWithNativeFg, gpu, Array.Empty<UserImportedDllRecord>());
        Assert.Equal(EnhancementProfilePreset.VisualQuality, profile.SelectedPreset);
        Assert.False(profile.EnableFrameGeneration); // Still blocked by Double-FG guard!

        CapabilityEngine.ApplyPreset(profile, EnhancementProfilePreset.SmootherGameplay, gameWithNativeFg, gpu, Array.Empty<UserImportedDllRecord>());
        Assert.Equal(UpscalingQualityPreset.Balanced, profile.SelectedUpscalingPreset);
        Assert.False(profile.EnableNeuralRendering);

        // Anti-cheat blocked game must be rejected at installer pre-flight until offline mode confirmed
        string tempDir = Path.Combine(Path.GetTempPath(), "LumaAc_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            Directory.CreateDirectory(tempDir);
            string acExe = Path.Combine(tempDir, "ac.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), acExe);
            var logger = new LumaLogger(Path.Combine(tempDir, "logs"));
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempDir, "backups"));
            var acGame = new DiscoveredGame
            {
                Id = "ac-game",
                Title = "Online AC Game",
                ExecutablePath = acExe,
                ExecutableDirectory = tempDir,
                InstallDirectory = tempDir,
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true,
                AntiCheatStatus = AntiCheatStatus.DetectedBlocked
            };
            var plan = new BackendInstallPlan { BackendId = "optiscaler", BackendVersion = "v0.9.5" };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                installer.InstallOrUpdateAsync(acGame, new GameEnhancementProfile { OfflineAntiCheatConfirmed = false }, plan));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ComponentResolver_IdentifiesMissingUserOwnedDependencyAndSupportsCancel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaResolver_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            string dbPath = Path.Combine(tempRoot, "luma.db");
            string gameDir = Path.Combine(tempRoot, "TempDx12Game");
            string stageDir = Path.Combine(tempRoot, "stage", "optiscaler");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(stageDir);

            string sampleExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            string gameExe = Path.Combine(gameDir, "Game.exe");
            File.Copy(sampleExe, gameExe);
            await File.WriteAllBytesAsync(Path.Combine(stageDir, "OptiScaler.dll"), new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, dbPath);
            await repo.InitializeAsync();
            var pkgMgr = new GitHubPackageManager(repo, logger, stagingRootDirectory: Path.Combine(tempRoot, "stage"));
            var manifests = await pkgMgr.GetManifestsAsync();
            var opti = manifests.First(m => m.Id == "optiscaler");
            opti.StagedInstallPath = stageDir;
            opti.CurrentInstalledVersion = opti.PinnedStableVersion;
            await repo.SaveBackendManifestsAsync(manifests);

            var vault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));
            var orchestrator = new BackendOrchestrator();
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "backups"));
            string emptyDownloads = Path.Combine(tempRoot, "empty_downloads");
            Directory.CreateDirectory(emptyDownloads);
            var resolver = new IntelligentComponentResolver(pkgMgr, vault, repo, orchestrator, installer, logger, emptyDownloads);

            var gpu = CreateRx6600Gpu();
            var game = new DiscoveredGame
            {
                Id = "temp-dx12",
                Title = "Temp DX12 Game",
                ExecutablePath = gameExe,
                ExecutableDirectory = gameDir,
                InstallDirectory = gameDir,
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true,
                NativeDlssSupported = true,
                NativeFsrSupported = true
            };

            // 1. Requiring Neural Rendering without nvngx_dlssnr.dll flags missing user-owned component
            var nrProfile = new CapabilityEngine().EvaluateGame(game, gpu, Array.Empty<UserImportedDllRecord>(), manifests, null, UpdateChannel.Stable);
            nrProfile.EnableNeuralRendering = true;
            var plan = await resolver.AnalyzeRequirementsAsync(game, nrProfile, gpu);
            Assert.True(plan.RequiresUserAttention);
            Assert.Contains(plan.Components, c => c.ComponentId == "nvngx_dlssnr" && !c.IsSatisfied);

            // 2. Cancelled setup leaves game directory completely untouched
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                resolver.ExecuteGuidedSetupAsync(game, nrProfile, gpu, null, cts.Token));
            Assert.False(File.Exists(Path.Combine(gameDir, "dxgi.dll")));

            // 3. Running guided setup with staged OptiScaler applies enhancement & creates recovery backup
            var steps = new List<SetupProgressStep>();
            var progress = new Progress<SetupProgressStep>(s => steps.Add(s));
            bool ok = await resolver.ExecuteGuidedSetupAsync(game, nrProfile, gpu, progress);
            Assert.True(ok);
            Assert.Equal(FriendlyGameStatus.Enhanced, game.FriendlyStatus);
            Assert.Contains(steps, s => s.State == SetupStepState.Complete && s.Label.Contains("recovery", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public void InstallerEngine_InstallRepairCancelUninstallLifecycle()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaSetupLifecycle_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            string payload = Path.Combine(tempRoot, "payload");
            string installDir = Path.Combine(tempRoot, "installed");
            Directory.CreateDirectory(Path.Combine(payload, "data", "gpu_profiles"));
            Directory.CreateDirectory(Path.Combine(payload, "data", "game_profiles"));
            Directory.CreateDirectory(Path.Combine(payload, "data", "backend_manifests"));

            // Fail payload check initially before required files exist
            Assert.False(InstallerEngine.VerifyPayload(payload, out _));

            File.WriteAllText(Path.Combine(payload, "ProjectLuma.exe"), "EXE_PLACEHOLDER");
            File.WriteAllText(Path.Combine(payload, "ProjectLuma.Updater.exe"), "UPDATER_PLACEHOLDER");
            File.WriteAllText(Path.Combine(payload, "data", "gpu_profiles", "amd_gpus.json"), "[]");
            File.WriteAllText(Path.Combine(payload, "data", "game_profiles", "game_compatibility.json"), "[]");
            File.WriteAllText(Path.Combine(payload, "data", "backend_manifests", "backends.json"), "[]");

            Assert.True(InstallerEngine.VerifyPayload(payload, out _));

            // Cancellation before install must clean up cleanly
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                InstallerEngine.Install(payload, installDir, createDesktopShortcut: false, null, cts.Token));
            Assert.False(Directory.Exists(installDir));

            // Clean Install
            InstallerEngine.Install(payload, installDir, createDesktopShortcut: false);
            Assert.True(File.Exists(Path.Combine(installDir, "ProjectLuma.exe")));
            Assert.True(File.Exists(Path.Combine(installDir, "install-manifest.json")));

            // Simulate user corruption / deletion and verify Repair restores exact content
            File.Delete(Path.Combine(installDir, "ProjectLuma.Updater.exe"));
            File.WriteAllText(Path.Combine(installDir, "data", "gpu_profiles", "amd_gpus.json"), "CORRUPTED");
            InstallerEngine.Repair(payload, installDir);
            Assert.True(File.Exists(Path.Combine(installDir, "ProjectLuma.Updater.exe")));
            Assert.Equal("[]", File.ReadAllText(Path.Combine(installDir, "data", "gpu_profiles", "amd_gpus.json")));

            // Clean Uninstall removes installation folder cleanly
            bool removed = InstallerEngine.Uninstall(installDir, removeShortcuts: false);
            Assert.True(removed);
            Assert.False(Directory.Exists(installDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ThousandGameLibrary_EvaluatesFiltersSortsAndChunksUnderThreshold()
    {
        var engine = new CapabilityEngine();
        var gpu = CreateRx6600Gpu();
        var sw = Stopwatch.StartNew();

        var cards = new List<GameCardViewModel>(1000);
        for (int i = 0; i < 1000; i++)
        {
            var game = new DiscoveredGame
            {
                Id = $"game-{i:D4}",
                Title = $"Benchmarked Title {i:D4}",
                ExecutablePath = $@"C:\Games\Game{i}\Game{i}.exe",
                ExecutableDirectory = $@"C:\Games\Game{i}",
                InstallDirectory = $@"C:\Games\Game{i}",
                Launcher = (GameLauncherType)(i % 6),
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true,
                NativeDlssSupported = i % 2 == 0,
                NativeFsrSupported = true
            };
            var profile = engine.EvaluateGame(game, gpu, Array.Empty<UserImportedDllRecord>(), Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);
            cards.Add(new GameCardViewModel(game, profile));
        }

        // Simulate instant filter + sort + 5-column virtual row building
        var filtered = cards
            .Where(c => c.FriendlyStatus == FriendlyGameStatus.ReadyToEnhance && c.Title.Contains("Title", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<GameCoverRow>(filtered.Count / 5 + 1);
        for (int i = 0; i < filtered.Count; i += 5)
            rows.Add(new GameCoverRow(filtered.Skip(i).Take(5).ToList()));

        sw.Stop();
        _output.WriteLine($"Evaluated + filtered + sorted + chunked 1,000 games in {sw.ElapsedMilliseconds} ms ({rows.Count} virtual rows)");
        Assert.Equal(1000, cards.Count);
        Assert.True(rows.Count >= 100);
        Assert.True(sw.ElapsedMilliseconds < 500, $"Expected 1,000-game pipeline < 500ms, actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void GpuTarget_And_CapabilityEngine_Rx6600_Gfx1032_ReportsTruthfulHip72Exclusion()
    {
        string target = WindowsGpuDetector.ResolveGpuTarget(0x1002, 0x73FF, GpuArchitecture.RDNA2, "Navi 23");
        Assert.Equal("gfx1032", target);

        var gpu = CreateRx6600Gpu(outdatedDriver: false, hip72: false);
        gpu.GpuTarget = target;
        gpu.Hip72OfficiallySupported = false;

        var game = new DiscoveredGame
        {
            Id = "rx6600-truth",
            Title = "Test DX12 Title",
            GraphicsApi = GraphicsApi.DirectX12,
            Is64Bit = true,
            NativeDlssSupported = true
        };

        string dummyNrPath = Path.Combine(Path.GetTempPath(), $"luma_nr_{Guid.NewGuid():N}.dll");
        File.WriteAllBytes(dummyNrPath, new byte[] { 0x4D, 0x5A });
        try
        {
            var htOnlyDll = new UserImportedDllRecord
            {
                FileName = "nvngx_dlssnr.dll",
                DllKind = "nvngx_dlssnr",
                FileVersion = "310.8.0.0",
                IsPe64Valid = true,
                StoredPath = dummyNrPath,
                SupportsCcModel = false
            };

            var engine = new CapabilityEngine();
            var profileMissingHip = engine.EvaluateGame(game, gpu, new[] { htOnlyDll }, Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);

            Assert.Equal(CapabilityState.RequiresDependency, profileMissingHip.NeuralRenderingState);
            Assert.Contains("gfx1032", profileMissingHip.NeuralRenderingReason, StringComparison.OrdinalIgnoreCase);

            var gpuWithExperimentalHip = CreateRx6600Gpu(outdatedDriver: false, hip72: true);
            gpuWithExperimentalHip.GpuTarget = target;
            gpuWithExperimentalHip.Hip72OfficiallySupported = false;

            var profileExperimental = engine.EvaluateGame(game, gpuWithExperimentalHip, new[] { htOnlyDll }, Array.Empty<BackendManifest>(), null, UpdateChannel.Stable);

            Assert.Equal(CapabilityState.Experimental, profileExperimental.NeuralRenderingState);
            Assert.False(profileExperimental.EnableNeuralRendering); // Opt-in by default on experimental Balanced preset
            CapabilityEngine.ApplyPreset(profileExperimental, EnhancementProfilePreset.VisualQuality, game, gpuWithExperimentalHip, new[] { htOnlyDll });
            Assert.True(profileExperimental.EnableNeuralRendering);
            Assert.Equal(CapabilityState.Unsupported, profileExperimental.RayReconstructionState);
        }
        finally
        {
            if (File.Exists(dummyNrPath)) File.Delete(dummyNrPath);
        }
    }

    [Fact]
    public async Task UserDllVault_ScopedGameFolderDiscovery_RespectsDepth3_And_CcModelThreshold()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaScanDll_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "test.db"));
            await repo.InitializeAsync();
            var vault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));

            string sysDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
            string gameRoot = Path.Combine(tempRoot, "Games");

            // Depth 2: valid 64-bit nvngx_dlss.dll -> MUST be discovered
            string depth2Dir = Path.Combine(gameRoot, "GameA", "Bin");
            Directory.CreateDirectory(depth2Dir);
            string depth2Dll = Path.Combine(depth2Dir, "nvngx_dlss.dll");
            File.Copy(sysDll, depth2Dll);

            // Depth 2 outdated version labeled nvngx_dlssnr.dll (10.0.x < 310.8) -> MUST be skipped cleanly without throwing
            File.Copy(sysDll, Path.Combine(depth2Dir, "nvngx_dlssnr.dll"));

            // Depth 5: beyond bounded depth 3 -> MUST NOT be scanned
            string depth5Dir = Path.Combine(gameRoot, "A", "B", "C", "D", "E");
            Directory.CreateDirectory(depth5Dir);
            File.Copy(sysDll, Path.Combine(depth5Dir, "nvngx_dlssg.dll"));

            var discovered = await vault.ScanGameLibrariesForUserDllsAsync(new[] { gameRoot });
            Assert.Single(discovered);
            Assert.Equal("nvngx_dlss.dll", discovered[0].FileName);
            Assert.True(discovered[0].IsPe64Valid);
            Assert.False(discovered[0].SupportsCcModel); // version.dll < 310.9.16.0
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public async Task PackageManager_DownloadAndStageDetailed_ReportsProgress_And_CleansUpPartialOnCancel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaDlTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string stageRoot = Path.Combine(tempRoot, "staging");
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "test.db"));
            await repo.InitializeAsync();

            byte[] zipPayload;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var entry = archive.CreateEntry("OptiScaler.dll");
                    await using var es = entry.Open();
                    await es.WriteAsync(Encoding.UTF8.GetBytes("OPTISCALER_MOCK_BINARY_CONTENT_FOR_STREAM_TEST"));
                }
                zipPayload = ms.ToArray();
            }

            using var okClient = new HttpClient(new StubBytesHttpHandler(zipPayload));
            var pm = new GitHubPackageManager(repo, logger, okClient, stageRoot);
            var manifests = await pm.GetManifestsAsync();
            var opti = manifests.First(m => m.Id == "optiscaler");

            var release = new BackendReleaseInfo
            {
                BackendId = "optiscaler",
                TagName = "v0.9.5",
                AssetName = "OptiScaler_v0.9.5.zip",
                AssetDownloadUrl = "https://github.com/OptiScaler/OptiScaler/releases/download/v0.9.5/OptiScaler_v0.9.5.zip",
                AssetSizeBytes = zipPayload.Length
            };

            var observed = new List<PackageOperationProgress>();
            var staged = await pm.DownloadAndStageDetailedAsync(opti, release, new SyncProgress<PackageOperationProgress>(observed.Add));

            Assert.Equal("v0.9.5", staged.CurrentInstalledVersion);
            Assert.Contains(observed, p => p.State == ComponentInstallState.Downloading);
            Assert.Contains(observed, p => p.State == ComponentInstallState.Verifying);
            Assert.Contains(observed, p => p.State == ComponentInstallState.Installed && p.Percent == 100);
            Assert.False(Directory.EnumerateFiles(stageRoot, "*.part", SearchOption.AllDirectories).Any());

            // Mid-stream cancellation cleans up any temporary .part file
            using var cancelCts = new CancellationTokenSource();
            using var cancellingClient = new HttpClient(new CancellingStreamHttpHandler(cancelCts));
            var pmCancel = new GitHubPackageManager(repo, logger, cancellingClient, stageRoot);
            var opti2 = (await pmCancel.GetManifestsAsync()).First(m => m.Id == "optiscaler");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pmCancel.DownloadAndStageDetailedAsync(opti2, release, new SyncProgress<PackageOperationProgress>(_ => { }), cancelCts.Token));

            Assert.False(Directory.EnumerateFiles(stageRoot, "*.part", SearchOption.AllDirectories).Any());
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public async Task GameLaunchService_LaunchesSteamUriAndDirectExe_WithTruthfulEnhancementSummary()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaLaunchTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string steamGameDir = Path.Combine(tempRoot, "SteamGame");
            string epicGameDir = Path.Combine(tempRoot, "EpicGame");
            Directory.CreateDirectory(steamGameDir);
            Directory.CreateDirectory(epicGameDir);

            string sysNotepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            string steamExe = Path.Combine(steamGameDir, "game.exe");
            string epicExe = Path.Combine(epicGameDir, "epicgame.exe");
            File.Copy(sysNotepad, steamExe);
            File.Copy(sysNotepad, epicExe);

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "backups"));
            var launcher = new GameLaunchService(installer, logger);

            ProcessStartInfo? captured = null;
            launcher.ProcessStarterOverride = psi => { captured = psi; return true; };

            var steamGame = new DiscoveredGame
            {
                Id = "steam-1",
                Title = "Steam Test Game",
                Launcher = GameLauncherType.Steam,
                StoreAppId = "1091500",
                ExecutablePath = steamExe,
                ExecutableDirectory = steamGameDir,
                InstallDirectory = steamGameDir,
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true
            };
            var profile = new GameEnhancementProfile { SelectedPreset = EnhancementProfilePreset.Balanced };

            var steamResult = await launcher.LaunchGameAsync(steamGame, profile);
            Assert.True(steamResult.Succeeded);
            Assert.Equal("steam://rungameid/1091500", captured?.FileName);
            Assert.Contains("Native Game Rendering", steamResult.ActiveEnhancementsSummary);

            var epicGame = new DiscoveredGame
            {
                Id = "epic-1",
                Title = "Epic Direct Title",
                Launcher = GameLauncherType.EpicGames,
                ExecutablePath = epicExe,
                ExecutableDirectory = epicGameDir,
                InstallDirectory = epicGameDir,
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true
            };
            var epicResult = await launcher.LaunchGameAsync(epicGame, profile);
            Assert.True(epicResult.Succeeded);
            Assert.Equal(epicExe, captured?.FileName);
            Assert.Equal(epicGameDir, captured?.WorkingDirectory);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task InstallerEngine_UninstallWithOptions_RestoresBackedUpGameFiles_And_PreservesUserModifiedFiles()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaUninstallOpts_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string appInstallDir = Path.Combine(tempRoot, "AppInstall");
            string appDataDir = Path.Combine(tempRoot, "LocalAppData");
            string backupsDir = Path.Combine(appDataDir, "backups");
            string logsDir = Path.Combine(appDataDir, "logs");
            string gameDir = Path.Combine(tempRoot, "MockGame");
            string stageDir = Path.Combine(tempRoot, "Stage");
            Directory.CreateDirectory(appInstallDir);
            Directory.CreateDirectory(appDataDir);
            Directory.CreateDirectory(logsDir);
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(stageDir);

            await File.WriteAllTextAsync(Path.Combine(appInstallDir, "ProjectLuma.exe"), "MOCK_APP");
            await File.WriteAllTextAsync(Path.Combine(appDataDir, "settings.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(logsDir, "app.log"), "LOG");

            string sysNotepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            string gameExe = Path.Combine(gameDir, "MockGame.exe");
            File.Copy(sysNotepad, gameExe);

            string dxgiPath = Path.Combine(gameDir, "dxgi.dll");
            string iniPath = Path.Combine(gameDir, "optiscaler.ini");
            await File.WriteAllTextAsync(dxgiPath, "ORIGINAL_GAME_DXGI");

            string srcDxgi = Path.Combine(stageDir, "dxgi.dll");
            string srcIni = Path.Combine(stageDir, "optiscaler.ini");
            await File.WriteAllTextAsync(srcDxgi, "LUMA_INSTALLED_DXGI");
            await File.WriteAllTextAsync(srcIni, "LUMA_DEFAULT_INI");

            var logger = new LumaLogger(logsDir);
            var gameInstaller = new TransactionalGameInstaller(new PeAnalyzer(), logger, backupsDir);
            var game = new DiscoveredGame
            {
                Id = "mock-uninstall-game",
                Title = "Mock Restore Game",
                ExecutablePath = gameExe,
                ExecutableDirectory = gameDir,
                InstallDirectory = gameDir,
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true
            };
            var plan = new BackendInstallPlan
            {
                BackendId = "optiscaler",
                BackendVersion = "v0.9.5",
                Features = { RenderingFeatureType.SuperResolution },
                FilesToDeploy =
                {
                    new BackendFileDeployItem { SourcePath = srcDxgi, RelativeTargetPath = "dxgi.dll" },
                    new BackendFileDeployItem { SourcePath = srcIni, RelativeTargetPath = "optiscaler.ini" }
                }
            };

            await gameInstaller.InstallOrUpdateAsync(game, new GameEnhancementProfile(), plan);
            Assert.Equal("LUMA_INSTALLED_DXGI", await File.ReadAllTextAsync(dxgiPath));

            // Simulate user customizing optiscaler.ini externally after install
            await File.WriteAllTextAsync(iniPath, "USER_MODIFIED_INI");

            var result = InstallerEngine.UninstallWithOptions(
                appInstallDir,
                restoreGameFiles: true,
                preserveBackups: true,
                removeUserPreferencesAndCache: true,
                removeShortcuts: false,
                removeRegistryEntry: false,
                appDataOverride: appDataDir);

            Assert.True(result.Success);
            Assert.False(Directory.Exists(appInstallDir));
            Assert.Equal("ORIGINAL_GAME_DXGI", await File.ReadAllTextAsync(dxgiPath));
            Assert.Equal("USER_MODIFIED_INI", await File.ReadAllTextAsync(iniPath)); // Modified file safely preserved
            Assert.True(Directory.Exists(backupsDir)); // Central backups preserved
            Assert.False(File.Exists(Path.Combine(appDataDir, "settings.json")));
            Assert.False(Directory.Exists(logsDir));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private sealed class StubBytesHttpHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            msg.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(msg);
        }
    }

    private sealed class CancellingStreamHttpHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var msg = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellingReadStream(cts))
            };
            msg.Content.Headers.ContentLength = 65536;
            return Task.FromResult(msg);
        }

        private sealed class CancellingReadStream(CancellationTokenSource cts) : Stream
        {
            private int _reads;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 65536;
            public override long Position { get => _reads * 1024; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                _reads++;
                if (_reads == 1)
                {
                    buffer.Span[..Math.Min(1024, buffer.Length)].Fill(0x41);
                    return Math.Min(1024, buffer.Length);
                }
                cts.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(10, cancellationToken);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task UiAutomation_ClickDownloadOfficial_RemainsResponsive_UpdatesProgress_SupportsCancel_And_HandlesInvalidArchive()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaUiAutoTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();

            byte[] validZipBytes;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var e = archive.CreateEntry("OptiScaler.dll");
                    await using var es = e.Open();
                    var content = new byte[200 * 1024];
                    Encoding.UTF8.GetBytes("OPTISCALER_UI_TEST").CopyTo(content, 0);
                    await es.WriteAsync(content);
                }
                validZipBytes = ms.ToArray();
            }

            var handler = new ControlledOptiScalerHttpHandler(validZipBytes);
            using var http = new HttpClient(handler);
            var pkgMgr = new GitHubPackageManager(repo, logger, http, Path.Combine(tempRoot, "backends"));
            var dllVault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));
            var orchestrator = new BackendOrchestrator();
            var pe = new PeAnalyzer();
            var installer = new TransactionalGameInstaller(pe, logger, Path.Combine(tempRoot, "backups"));
            var resolver = new IntelligentComponentResolver(pkgMgr, dllVault, repo, orchestrator, installer, logger);
            var scanner = new MultiStoreGameScanner(logger, pe);
            var artwork = new ArtworkDiscoveryService(logger, cacheDirectory: Path.Combine(tempRoot, "art"), credFilePath: Path.Combine(tempRoot, "sgdb.dat"));
            var launcher = new GameLaunchService(installer, logger);
            var gpuDetector = new StubGpuDetector(CreateRx6600Gpu());
            var updater = new UpdateCoordinatorService(gpuDetector, repo, pkgMgr, orchestrator, installer, logger);
            var vm = new MainViewModel(
                gpuDetector, new CapabilityEngine(), repo, scanner, artwork,
                pkgMgr, dllVault, orchestrator, resolver, installer, launcher,
                updater, logger, new StubDialogService());

            await vm.InitializeAsync(autoScan: false, showOnboardingIfFirstRun: false);

            await RunOnWpfStaAsync(dispatcher =>
            {
                var view = new EnhancementCentreView { DataContext = vm };
                var hostWin = new Window
                {
                    Width = 1200,
                    Height = 760,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = view
                };
                hostWin.Show();
                PumpDispatcher(dispatcher, TimeSpan.FromMilliseconds(80));

                try
                {
                    hostWin.UpdateLayout();
                    PumpDispatcher(dispatcher, TimeSpan.FromMilliseconds(50));

                    var optiItem = vm.ComponentItems.First(c => c.Id == "optiscaler");

                    // 1. Corrupt package error recovery via real WPF button click
                    handler.Mode = ControlledOptiScalerHttpHandler.DownloadMode.CorruptBytes;
                    var downloadBtn = FindComponentButton(view, "optiscaler", "Install", "Retry", "Reinstall", "Download Official", "Retry Download", "Reinstall Official", "Update");
                    Assert.NotNull(downloadBtn);
                    InvokeWpfButton(downloadBtn!);

                    PumpUntil(dispatcher, () => !optiItem.IsBusy && optiItem.IsFailed, TimeSpan.FromSeconds(5));
                    Assert.True(optiItem.IsFailed);
                    Assert.False(string.IsNullOrWhiteSpace(optiItem.LastErrorText));
                    Assert.False(optiItem.IsInstalled);

                    // 2. Slow paused download -> prove UI dispatcher pumps responsive frames -> Cancel via WPF button
                    handler.Mode = ControlledOptiScalerHttpHandler.DownloadMode.PausingSlowStream;
                    handler.ResetPauseGate();
                    downloadBtn = FindComponentButton(view, "optiscaler", "Retry", "Install", "Reinstall", "Retry Download", "Download Official", "Update");
                    Assert.NotNull(downloadBtn);
                    InvokeWpfButton(downloadBtn!);

                    PumpUntil(dispatcher, () => optiItem.IsBusy && handler.StreamEntered, TimeSpan.FromSeconds(5));
                    int uiTicksWhileDownloading = 0;
                    for (int i = 0; i < 6; i++)
                    {
                        dispatcher.Invoke(() => uiTicksWhileDownloading++, DispatcherPriority.Background);
                        PumpDispatcher(dispatcher, TimeSpan.FromMilliseconds(20));
                    }
                    Assert.True(uiTicksWhileDownloading >= 6, "UI Dispatcher stalled during active download");

                    var cancelBtn = FindComponentButton(view, "optiscaler", "Cancel");
                    Assert.NotNull(cancelBtn);
                    InvokeWpfButton(cancelBtn!);
                    handler.ReleasePauseGate();

                    PumpUntil(dispatcher, () => !optiItem.IsBusy, TimeSpan.FromSeconds(5));
                    Assert.False(Directory.EnumerateFiles(pkgMgr.StagingRootDirectory, "*.part", SearchOption.AllDirectories).Any());

                    // 3. Valid package download -> live progress updates -> staged & verified
                    handler.Mode = ControlledOptiScalerHttpHandler.DownloadMode.ValidArchive;
                    double maxPctSeen = 0;
                    optiItem.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName == nameof(BackendComponentItem.DownloadProgressPercent) && optiItem.DownloadProgressPercent > maxPctSeen)
                            maxPctSeen = optiItem.DownloadProgressPercent;
                    };

                    downloadBtn = FindComponentButton(view, "optiscaler", "Install", "Retry", "Reinstall", "Download Official", "Retry Download", "Update");
                    Assert.NotNull(downloadBtn);
                    InvokeWpfButton(downloadBtn!);

                    PumpUntil(dispatcher, () => !optiItem.IsBusy && vm.ComponentItems.Any(c => c.Id == "optiscaler" && c.IsInstalled), TimeSpan.FromSeconds(8));
                    var refreshedOpti = vm.ComponentItems.First(c => c.Id == "optiscaler");
                    Assert.True(refreshedOpti.IsInstalled);
                    Assert.Equal("v0.9.5", refreshedOpti.CurrentInstalledVersion);
                    Assert.True(maxPctSeen > 0);
                    Assert.True(File.Exists(Path.Combine(refreshedOpti.Manifest.StagedInstallPath, "OptiScaler.dll")));
                }
                finally
                {
                    hostWin.Close();
                    vm.Dispose();
                }
            });
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public async Task DownloadsFolderWatcher_And_AutoDiscovery_ImportsValidDllAndArchiveAutomatically()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaWatchTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string downloadsDir = Path.Combine(tempRoot, "Downloads");
            Directory.CreateDirectory(downloadsDir);

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();
            var pkgMgr = new GitHubPackageManager(repo, logger, stagingRootDirectory: Path.Combine(tempRoot, "backends"));
            var dllVault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));
            var orchestrator = new BackendOrchestrator();
            var pe = new PeAnalyzer();
            var installer = new TransactionalGameInstaller(pe, logger, Path.Combine(tempRoot, "backups"));
            var resolver = new IntelligentComponentResolver(pkgMgr, dllVault, repo, orchestrator, installer, logger);
            var scanner = new MultiStoreGameScanner(logger, pe);
            var artwork = new ArtworkDiscoveryService(logger, cacheDirectory: Path.Combine(tempRoot, "art"), credFilePath: Path.Combine(tempRoot, "sgdb.dat"));
            var launcher = new GameLaunchService(installer, logger);
            var gpuDetector = new StubGpuDetector(CreateRx6600Gpu());
            var updater = new UpdateCoordinatorService(gpuDetector, repo, pkgMgr, orchestrator, installer, logger);
            using var vm = new MainViewModel(
                gpuDetector, new CapabilityEngine(), repo, scanner, artwork,
                pkgMgr, dllVault, orchestrator, resolver, installer, launcher,
                updater, logger, new StubDialogService());

            await vm.InitializeAsync(autoScan: false, showOnboardingIfFirstRun: false);

            // Start FileSystemWatcher on temp Downloads folder before dropping files
            vm.StartDownloadsFolderWatcher(downloadsDir);
            Assert.True(vm.IsDownloadsWatcherActive);

            // Drop valid 64-bit DLL + upstream user-acquired DLSS-NR-on-AMD archive into Downloads
            string sysDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
            File.Copy(sysDll, Path.Combine(downloadsDir, "nvngx_dlss.dll"));

            string archivePath = Path.Combine(downloadsDir, "DLSS-NR-on-AMD-v0.9.0.zip");
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var e = archive.CreateEntry("NeuralHub.dll");
                    await using var es = e.Open();
                    await es.WriteAsync(Encoding.UTF8.GetBytes("NEURALHUB_USER_BINARY"));
                }
                await File.WriteAllBytesAsync(archivePath, ms.ToArray());
            }

            var deadline = DateTime.UtcNow.AddSeconds(5);
            bool nrStaged = false;
            bool dllImported = false;
            while (DateTime.UtcNow < deadline)
            {
                var manifests = await pkgMgr.GetManifestsAsync();
                var nr = manifests.First(m => m.Id == "dlss-nr-on-amd");
                nrStaged = nr.CurrentInstalledVersion == "v0.9.0" && File.Exists(Path.Combine(nr.StagedInstallPath, "NeuralHub.dll"));
                dllImported = (await dllVault.GetImportedDllsAsync()).Any(d => d.FileName == "nvngx_dlss.dll");
                if (nrStaged && dllImported) break;
                await Task.Delay(120);
            }

            Assert.True(dllImported, "Expected Downloads watcher to auto-import nvngx_dlss.dll");
            Assert.True(nrStaged, "Expected Downloads watcher to auto-stage DLSS-NR-on-AMD-v0.9.0.zip");
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public async Task UpdateCoordinator_GeneratesGenuineComponentActions_And_NeverShowsInstallForAppOrIncompatibleBackend()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaUpdatesTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();
            var settings = await repo.LoadSettingsAsync();
            settings.AutoDownloadVerifiedUpdates = false;
            await repo.SaveSettingsAsync(settings);

            var handler = new ControlledOptiScalerHttpHandler(Array.Empty<byte>());
            using var http = new HttpClient(handler);
            var pkgMgr = new GitHubPackageManager(repo, logger, http, Path.Combine(tempRoot, "backends"));
            var gpuDetector = new StubGpuDetector(CreateRx6600Gpu());
            var orchestrator = new BackendOrchestrator();
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "backups"));

            var coordinator = new UpdateCoordinatorService(gpuDetector, repo, pkgMgr, orchestrator, installer, logger);
            await coordinator.CheckNowAsync();

            var appItem = coordinator.Status.Items.First(i => i.ComponentId == "project-luma-app");
            Assert.False(appItem.ShowActionButton);
            Assert.Equal("Installed", appItem.StateBadgeText);

            var amdlss5 = coordinator.Status.Items.First(i => i.ComponentId == "amdlss5-reference");
            Assert.False(amdlss5.ShowActionButton);
            Assert.Equal("Not compatible", amdlss5.StateBadgeText);
            Assert.Contains("RDNA2", amdlss5.Status);

            var optiBefore = coordinator.Status.Items.First(i => i.ComponentId == "optiscaler");
            Assert.True(optiBefore.ShowActionButton);
            Assert.Equal("Download Official", optiBefore.ActionLabel);
            Assert.Equal("Ready to configure", optiBefore.StateBadgeText);

            var nrItem = coordinator.Status.Items.First(i => i.ComponentId == "dlss-nr-on-amd");
            Assert.True(nrItem.ShowActionButton);
            Assert.Equal("Official Release", nrItem.ActionLabel);
            Assert.Equal("Guided setup", nrItem.StateBadgeText);

            var afmfItem = coordinator.Status.Items.First(i => i.ComponentId == "amd-afmf-guide");
            Assert.True(afmfItem.ShowActionButton);
            Assert.Equal("Adrenalin Guide", afmfItem.ActionLabel);
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public async Task GameLaunchService_SupportsAllEightLaunchers_WithProtocolAndPhaseTracking()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaEightLaunchers_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            string dummyExe = Path.Combine(tempRoot, "DummyGame.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), dummyExe);

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var pe = new PeAnalyzer();
            var installer = new TransactionalGameInstaller(pe, logger, Path.Combine(tempRoot, "backups"));
            ProcessStartInfo? capturedPsi = null;
            var launcher = new GameLaunchService(installer, logger)
            {
                LaunchConfirmationTimeoutMs = 0,
                ProcessStarterOverride = psi => { capturedPsi = psi; return true; }
            };
            var profile = new GameEnhancementProfile { SelectedPreset = EnhancementProfilePreset.Balanced };

            var protocolCases = new (GameLauncherType Launcher, string AppId, string ExpectedPrefix)[]
            {
                (GameLauncherType.Steam, "1091500", "steam://rungameid/1091500"),
                (GameLauncherType.EpicGames, "AlanWake2", "com.epicgames.launcher://apps/AlanWake2?action=launch&silent=true"),
                (GameLauncherType.UbisoftConnect, "13504", "uplay://launch/13504/0"),
                (GameLauncherType.EaApp, "Origin.OFR.50.0001", "origin2://game/launch?offerIds=Origin.OFR.50.0001"),
                (GameLauncherType.BattleNet, "Pro", "battlenet://Pro"),
                (GameLauncherType.Xbox, "Microsoft.FH5_8wekyb3d8bbwe!Forza", "explorer.exe")
            };

            foreach (var tc in protocolCases)
            {
                capturedPsi = null;
                var game = new DiscoveredGame
                {
                    Id = $"g-{tc.Launcher}",
                    Title = $"Game {tc.Launcher}",
                    Launcher = tc.Launcher,
                    StoreAppId = tc.AppId,
                    ExecutablePath = dummyExe,
                    ExecutableDirectory = tempRoot,
                    InstallDirectory = tempRoot,
                    GraphicsApi = GraphicsApi.DirectX12,
                    Is64Bit = true
                };

                var res = await launcher.LaunchGameAsync(game, profile);
                Assert.True(res.Succeeded);
                Assert.Equal(GameLaunchPhase.LauncherInvoked, res.Phase);
                Assert.NotNull(capturedPsi);
                Assert.Equal(tc.ExpectedPrefix, capturedPsi!.FileName);
                if (tc.Launcher == GameLauncherType.Xbox)
                    Assert.Contains(tc.AppId, capturedPsi.Arguments);
            }

            // GOG fallback without Galaxy client & Manual always fall back cleanly to executable with Running phase
            foreach (var directLauncher in new[] { GameLauncherType.GogGalaxy, GameLauncherType.Manual })
            {
                capturedPsi = null;
                var game = new DiscoveredGame
                {
                    Id = $"g-{directLauncher}",
                    Title = $"Game {directLauncher}",
                    Launcher = directLauncher,
                    StoreAppId = directLauncher == GameLauncherType.GogGalaxy ? "1207658924" : string.Empty,
                    ExecutablePath = dummyExe,
                    ExecutableDirectory = tempRoot,
                    InstallDirectory = tempRoot,
                    GraphicsApi = GraphicsApi.DirectX12,
                    Is64Bit = true
                };

                var res = await launcher.LaunchGameAsync(game, profile);
                Assert.True(res.Succeeded);
                Assert.NotNull(capturedPsi);
                if (directLauncher == GameLauncherType.Manual)
                {
                    Assert.Equal(dummyExe, capturedPsi!.FileName);
                    Assert.Equal(GameLaunchPhase.Running, res.Phase);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task NeuralRendering_EightStates_StrictDllSeparation_And_HipProbeTruthfulness()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaNrStates_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();
            var dllVault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));

            // Strict DLL separation: nvngx_dlss.dll NEVER becomes nvngx_dlssnr
            string sysDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
            string dlssCopy = Path.Combine(tempRoot, "nvngx_dlss.dll");
            File.Copy(sysDll, dlssCopy);
            var dlssRec = await dllVault.ImportDllAsync(dlssCopy);
            Assert.Equal("nvngx_dlss", dlssRec.DllKind);

            string dummyNrDllPath = Path.Combine(tempRoot, "nvngx_dlssnr.dll");
            await File.WriteAllBytesAsync(dummyNrDllPath, new byte[] { 0x4D, 0x5A });
            string nrStageDir = Path.Combine(tempRoot, "dlss-nr-stage");
            Directory.CreateDirectory(nrStageDir);
            await File.WriteAllTextAsync(Path.Combine(nrStageDir, "NeuralHub.dll"), "NR");

            var manifests = new List<BackendManifest>
            {
                new() { Id = "optiscaler", PinnedStableVersion = "v0.9.5" },
                new() { Id = "dlss-nr-on-amd", PinnedStableVersion = "v0.9.0", StagedInstallPath = nrStageDir }
            };
            var engine = new CapabilityEngine();
            var baseGame = new DiscoveredGame
            {
                Id = "nr-test",
                Title = "NR Probe Game",
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true,
                NativeDlssSupported = true,
                NativeFsrSupported = true
            };

            // 1. Unsupported (DX11 -> not DX12)
            var dx11Game = new DiscoveredGame
            {
                Id = "nr-dx11",
                Title = "DX11 Game",
                GraphicsApi = GraphicsApi.DirectX11,
                Is64Bit = true
            };
            var pUnsupported = engine.EvaluateGame(dx11Game, CreateRx6600Gpu(), Array.Empty<UserImportedDllRecord>(), manifests, null, UpdateChannel.Stable);
            Assert.Equal(NeuralRenderingActivationState.Unsupported, pUnsupported.NeuralActivationState);

            // 2. RuntimeMissing on RX 6600 when HIP 6 initializes cleanly but HIP 7.2 SDK is absent
            var gpuHip6Only = CreateRx6600Gpu(outdatedDriver: false, hip72: false);
            gpuHip6Only.HipRuntimeInitialized = true;
            gpuHip6Only.HipRuntimeApiVersion = 60450101;
            var pRuntimeMissing = engine.EvaluateGame(baseGame, gpuHip6Only, Array.Empty<UserImportedDllRecord>(), manifests, null, UpdateChannel.Stable);
            Assert.Equal(NeuralRenderingActivationState.RuntimeMissing, pRuntimeMissing.NeuralActivationState);
            Assert.Contains("gfx1032", pRuntimeMissing.NeuralActivationExplanation);
            Assert.Contains("v0.9.0", pRuntimeMissing.NeuralActivationExplanation);

            // 3. RuntimeFailed when HIP runtime DLL failed device initialization
            var gpuHipFailed = CreateRx6600Gpu(outdatedDriver: false, hip72: true);
            gpuHipFailed.HipRuntimeInstalled = true;
            gpuHipFailed.HipRuntimeInitialized = false;
            gpuHipFailed.HipInitDiagnostic = "hipGetDeviceCount failed on test adapter.";
            var pRuntimeFailed = engine.EvaluateGame(baseGame, gpuHipFailed, Array.Empty<UserImportedDllRecord>(), manifests, null, UpdateChannel.Stable);
            Assert.Equal(NeuralRenderingActivationState.RuntimeFailed, pRuntimeFailed.NeuralActivationState);

            // 4. DependencyMissing even with nvngx_dlss.dll in vault (must require nvngx_dlssnr.dll specifically)
            var gpuWithHip72 = CreateRx6600Gpu(outdatedDriver: false, hip72: true);
            gpuWithHip72.HipRuntimeInitialized = true;
            var pDepMissing = engine.EvaluateGame(baseGame, gpuWithHip72, new[] { dlssRec }, manifests, null, UpdateChannel.Stable);
            Assert.Equal(NeuralRenderingActivationState.DependencyMissing, pDepMissing.NeuralActivationState);
            Assert.Contains("nvngx_dlssnr.dll", pDepMissing.NeuralActivationExplanation);

            // 5. ReadyToConfigure when both DLSS-NR-on-AMD v0.9.0 AND nvngx_dlssnr.dll >= 310.9.16.0 exist
            var nrRecordCc = new UserImportedDllRecord
            {
                FileName = "nvngx_dlssnr.dll",
                DllKind = "nvngx_dlssnr",
                FileVersion = "310.9.16.0",
                SupportsCcModel = true,
                StoredPath = dummyNrDllPath
            };
            var pReadyCc = engine.EvaluateGame(baseGame, gpuWithHip72, new[] { nrRecordCc }, manifests, null, UpdateChannel.Stable);
            Assert.Equal(NeuralRenderingActivationState.ReadyToConfigure, pReadyCc.NeuralActivationState);
            Assert.Contains("CC & HT", pReadyCc.NeuralActivationExplanation);

            // 6. Active when game already has NeuralRendering installed and Healthy
            var activeGame = new DiscoveredGame
            {
                Id = "nr-active",
                Title = "Active NR Game",
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true,
                NativeDlssSupported = true,
                HealthState = IntegrationHealthState.Healthy,
                InstalledFeatures = { RenderingFeatureType.NeuralRendering }
            };
            var pActive = engine.EvaluateGame(activeGame, gpuWithHip72, new[] { nrRecordCc }, manifests, null, UpdateChannel.Stable);
            Assert.Equal(NeuralRenderingActivationState.Active, pActive.NeuralActivationState);

            // Safe P/Invoke probe check on non-existent runtime path
            var missingProbeInfo = new DetectedGpuInfo();
            WindowsGpuDetector.ProbeHipInitialization(Path.Combine(tempRoot, "missing_hip.dll"), missingProbeInfo);
            Assert.False(missingProbeInfo.HipRuntimeInitialized);

            // Live host AMD RX 6600 verification (assert truth constraints: gfx1032 NEVER marked officially HIP 7.2 supported)
            var liveGpu = await new WindowsGpuDetector(logger).DetectPrimaryGpuAsync();
            if (liveGpu.GpuTarget.Equals("gfx1032", StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(liveGpu.Hip72OfficiallySupported);
                Assert.True(liveGpu.HipRuntimeInitialized);
            }
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public async Task GitHubPackageManager_EnforcesDigestSha256_And_SupportsUpdateCentreUiDownloadAndCancel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaUpdateUiTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();
            var settings = await repo.LoadSettingsAsync();
            settings.AutoDownloadVerifiedUpdates = false;
            await repo.SaveSettingsAsync(settings);

            byte[] validZipBytes;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var e = archive.CreateEntry("OptiScaler.dll");
                    await using var es = e.Open();
                    var content = new byte[128 * 1024];
                    Encoding.UTF8.GetBytes("OPTISCALER_UPDATER_UI_TEST").CopyTo(content, 0);
                    await es.WriteAsync(content);
                }
                validZipBytes = ms.ToArray();
            }

            var handler = new ControlledOptiScalerHttpHandler(validZipBytes)
            {
                DigestSha256Override = new string('0', 64) // Deliberate mismatch for digest verification test
            };
            using var http = new HttpClient(handler);
            var pkgMgr = new GitHubPackageManager(repo, logger, http, Path.Combine(tempRoot, "backends"));
            var optiManifest = (await pkgMgr.GetManifestsAsync()).First(m => m.Id == "optiscaler");

            var badRelease = await pkgMgr.CheckLatestReleaseAsync(optiManifest, UpdateChannel.Stable);
            Assert.NotNull(badRelease);
            Assert.Equal(new string('0', 64), badRelease!.AssetExpectedSha256);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pkgMgr.DownloadAndStageAsync(optiManifest, badRelease));

            // Clear digest override and drive UpdateCentreView download + cancel + complete via STA WPF
            handler.DigestSha256Override = null;
            var dllVault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));
            var orchestrator = new BackendOrchestrator();
            var pe = new PeAnalyzer();
            var installer = new TransactionalGameInstaller(pe, logger, Path.Combine(tempRoot, "backups"));
            var resolver = new IntelligentComponentResolver(pkgMgr, dllVault, repo, orchestrator, installer, logger);
            var scanner = new MultiStoreGameScanner(logger, pe);
            var artwork = new ArtworkDiscoveryService(logger, cacheDirectory: Path.Combine(tempRoot, "art"), credFilePath: Path.Combine(tempRoot, "sgdb.dat"));
            var launcher = new GameLaunchService(installer, logger);
            var gpuDetector = new StubGpuDetector(CreateRx6600Gpu());
            var updater = new UpdateCoordinatorService(gpuDetector, repo, pkgMgr, orchestrator, installer, logger);
            var vm = new MainViewModel(
                gpuDetector, new CapabilityEngine(), repo, scanner, artwork,
                pkgMgr, dllVault, orchestrator, resolver, installer, launcher,
                updater, logger, new StubDialogService());

            await vm.InitializeAsync(autoScan: false, showOnboardingIfFirstRun: false);

            await RunOnWpfStaAsync(dispatcher =>
            {
                var view = new UpdateCentreView { DataContext = vm };
                var hostWin = new Window
                {
                    Width = 1200,
                    Height = 760,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                    Content = view
                };
                hostWin.Show();
                PumpDispatcher(dispatcher, TimeSpan.FromMilliseconds(80));

                try
                {
                    vm.CheckUpdatesNowCommand.Execute(null);
                    PumpUntil(dispatcher, () => vm.UpdateItems.Any(u => u.ComponentId == "optiscaler"), TimeSpan.FromSeconds(5));
                    PumpDispatcher(dispatcher, TimeSpan.FromMilliseconds(40));

                    var updateItem = vm.UpdateItems.First(u => u.ComponentId == "optiscaler");

                    // Start slow paused download from Downloads view and cancel cleanly
                    handler.Mode = ControlledOptiScalerHttpHandler.DownloadMode.PausingSlowStream;
                    handler.ResetPauseGate();
                    var dlBtn = FindUpdateItemButton(view, "optiscaler", "Download Official", "Update", "Verify");
                    Assert.NotNull(dlBtn);
                    InvokeWpfButton(dlBtn!);

                    PumpUntil(dispatcher, () => updateItem.IsBusy && handler.StreamEntered, TimeSpan.FromSeconds(5));
                    var cancelBtn = FindUpdateItemButton(view, "optiscaler", "Cancel");
                    Assert.NotNull(cancelBtn);
                    InvokeWpfButton(cancelBtn!);
                    handler.ReleasePauseGate();

                    PumpUntil(dispatcher, () => !updateItem.IsBusy, TimeSpan.FromSeconds(5));
                    Assert.False(Directory.EnumerateFiles(pkgMgr.StagingRootDirectory, "*.part", SearchOption.AllDirectories).Any());

                    // Valid download via UpdateCentreView
                    handler.Mode = ControlledOptiScalerHttpHandler.DownloadMode.ValidArchive;
                    dlBtn = FindUpdateItemButton(view, "optiscaler", "Download Official", "Update", "Verify", "Retry");
                    Assert.NotNull(dlBtn);
                    InvokeWpfButton(dlBtn!);

                    PumpUntil(dispatcher, () => !updateItem.IsBusy && vm.ComponentItems.Any(c => c.Id == "optiscaler" && c.IsInstalled), TimeSpan.FromSeconds(8));
                    var stagedOpti = vm.ComponentItems.First(c => c.Id == "optiscaler");
                    Assert.True(stagedOpti.IsInstalled);
                    string dllPath = Path.Combine(stagedOpti.Manifest.StagedInstallPath, "OptiScaler.dll");
                    Assert.True(File.Exists(dllPath));

                    // Delete file and invoke RepairBackendCommand synchronously on dispatcher
                    File.Delete(dllPath);
                    Assert.False(File.Exists(dllPath));
                    vm.RepairBackendCommand.Execute(stagedOpti);
                    PumpUntil(dispatcher, () => !stagedOpti.IsBusy && File.Exists(dllPath), TimeSpan.FromSeconds(8));
                    Assert.True(File.Exists(dllPath));
                }
                finally
                {
                    hostWin.Close();
                    vm.Dispose();
                }
            });
        }
        finally
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(120);
                }
            }
        }
    }

    [Fact]
    public async Task InstallerEngine_CleanInstall_Upgrade_CorruptFileRepair_And_MissingPayloadRejection()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaInstRepair_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string payloadDir = Path.Combine(tempRoot, "payload");
            string installDir = Path.Combine(tempRoot, "install");
            Directory.CreateDirectory(payloadDir);
            Directory.CreateDirectory(installDir);

            await File.WriteAllTextAsync(Path.Combine(payloadDir, "ProjectLuma.exe"), "EXE_V320");
            await File.WriteAllTextAsync(Path.Combine(payloadDir, "ProjectLuma.dll"), "DLL_V320");
            await File.WriteAllTextAsync(Path.Combine(payloadDir, "ProjectLuma.deps.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(payloadDir, "ProjectLuma.runtimeconfig.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(payloadDir, "project-luma.ico"), "ICO");

            Assert.True(InstallerEngine.VerifyPayload(payloadDir, out _));
            Assert.False(InstallerEngine.VerifyPayload(Path.Combine(tempRoot, "empty_dir"), out _));

            // Simulate prior v3.1.0 installation file before upgrade install
            await File.WriteAllTextAsync(Path.Combine(installDir, "install-manifest.json"), "{\"Version\":\"3.1.0\",\"FileCount\":1}");

            InstallerEngine.Install(
                payloadDir,
                installDir,
                createDesktopShortcut: false,
                progress: null,
                ct: CancellationToken.None,
                createStartMenuShortcut: false,
                registerInWindows: false);

            var summary = InstallerEngine.InspectInstallationState(installDir, appDataOverride: Path.Combine(tempRoot, "appdata"));
            Assert.Equal(InstallerEngine.ProductVersion, summary.InstalledVersion);
            Assert.True(File.Exists(Path.Combine(installDir, "ProjectLuma.dll")));

            // Delete core DLL and verify Repair restores it
            File.Delete(Path.Combine(installDir, "ProjectLuma.dll"));
            InstallerEngine.Repair(payloadDir, installDir, registerInWindows: false);
            Assert.Equal("DLL_V320", await File.ReadAllTextAsync(Path.Combine(installDir, "ProjectLuma.dll")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static readonly object WpfResLock = new();
    private static Dispatcher? _sharedStaDispatcher;
    private static bool _wpfResLoaded;

    private static Task RunOnWpfStaAsync(Action<Dispatcher> action)
    {
        Dispatcher disp;
        lock (WpfResLock)
        {
            if (_sharedStaDispatcher == null || _sharedStaDispatcher.HasShutdownStarted || !_sharedStaDispatcher.Thread.IsAlive)
            {
                var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    var d = Dispatcher.CurrentDispatcher;
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(d));
                    ready.SetResult(d);
                    Dispatcher.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                _sharedStaDispatcher = ready.Task.GetAwaiter().GetResult();
            }
            disp = _sharedStaDispatcher;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        disp.BeginInvoke(() =>
        {
            try
            {
                EnsureWpfResourcesLoaded();
                action(disp);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    private static void EnsureWpfResourcesLoaded()
    {
        lock (WpfResLock)
        {
            if (Application.Current == null)
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            if (_wpfResLoaded) return;
            foreach (string uriStr in new[]
                     {
                         "pack://application:,,,/ProjectLuma.UI;component/Themes/LumaTokens.xaml",
                         "pack://application:,,,/ProjectLuma.UI;component/Themes/Icons.xaml"
                     })
            {
                Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uriStr, UriKind.Absolute) });
            }
            Application.Current!.Resources["CapabilityStateBrushConverter"] = new CapabilityStateBrushConverter();
            Application.Current!.Resources["FriendlyStatusBrushConverter"] = new FriendlyStatusBrushConverter();
            Application.Current!.Resources["SetupStepBrushConverter"] = new SetupStepBrushConverter();
            Application.Current!.Resources["CapabilityStateLabelConverter"] = new CapabilityStateLabelConverter();
            Application.Current!.Resources["FilePathToBitmapConverter"] = new FilePathToBitmapConverter();
            Application.Current!.Resources["BoolToVisibilityConverter"] = new BoolToVisibilityConverter();
            Application.Current!.Resources["EnumEqualsConverter"] = new EnumEqualsConverter();
            Application.Current!.Resources["StringEqualsMultiConverter"] = new StringEqualsMultiConverter();
            Application.Current!.Resources["InverseBoolConverter"] = new InverseBoolConverter();
            Application.Current!.Resources["ObjectReferenceEqualsMultiConverter"] = new ObjectReferenceEqualsMultiConverter();
            Application.Current!.Resources["ComponentStateBrushConverter"] = new ComponentStateBrushConverter();
            Application.Current!.Resources["NeuralActivationBrushConverter"] = new NeuralActivationBrushConverter();
            _wpfResLoaded = true;
        }
    }

    private static void PumpDispatcher(Dispatcher dispatcher, TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
    }

    private static void PumpUntil(Dispatcher dispatcher, Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (condition()) return;
            Thread.Sleep(15);
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var sub in FindVisualDescendants<T>(child))
                yield return sub;
        }
    }

    private static Button? FindComponentButton(DependencyObject root, string componentId, params string[] candidateLabels)
    {
        foreach (var btn in FindVisualDescendants<Button>(root))
        {
            if (btn.Visibility != Visibility.Visible) continue;
            if (btn.CommandParameter is BackendComponentItem item &&
                item.Id.Equals(componentId, StringComparison.OrdinalIgnoreCase) &&
                btn.Content is string text &&
                candidateLabels.Any(l => text.Equals(l, StringComparison.OrdinalIgnoreCase)))
            {
                return btn;
            }
        }
        return null;
    }

    private static Button? FindUpdateItemButton(DependencyObject root, string componentId, params string[] candidateLabels)
    {
        foreach (var btn in FindVisualDescendants<Button>(root))
        {
            if (btn.Visibility != Visibility.Visible) continue;
            if (btn.CommandParameter is UpdateCheckItem item &&
                item.ComponentId.Equals(componentId, StringComparison.OrdinalIgnoreCase) &&
                btn.Content is string text &&
                candidateLabels.Any(l => text.Equals(l, StringComparison.OrdinalIgnoreCase)))
            {
                return btn;
            }
        }
        return null;
    }

    private static void InvokeWpfButton(Button button)
    {
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
            invoker.Invoke();
        else
            button.Command?.Execute(button.CommandParameter);
    }

    private sealed class StubGpuDetector(DetectedGpuInfo info) : IGpuDetector
    {
        public Task<DetectedGpuInfo> DetectPrimaryGpuAsync(CancellationToken ct = default) => Task.FromResult(info);
        public IReadOnlyList<GpuCatalogEntry> GetCatalogEntries() => Array.Empty<GpuCatalogEntry>();
    }

    private sealed class StubDialogService : IUserDialogService
    {
        public string? PickFolder(string title) => null;
        public string? PickFile(string title, string filter) => null;
        public string? SaveFile(string title, string defaultFileName, string filter) => null;
        public void OpenUrl(string url) { }
        public void OpenFolder(string path) { }
    }

    private sealed class ControlledOptiScalerHttpHandler(byte[] validZipBytes) : HttpMessageHandler
    {
        public enum DownloadMode { ValidArchive, CorruptBytes, PausingSlowStream }

        public DownloadMode Mode { get; set; } = DownloadMode.ValidArchive;
        public string? DigestSha256Override { get; set; }
        public bool StreamEntered { get; private set; }
        private TaskCompletionSource _pauseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ResetPauseGate()
        {
            StreamEntered = false;
            _pauseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleasePauseGate() => _pauseGate.TrySetResult();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            {
                string digestField = string.IsNullOrWhiteSpace(DigestSha256Override)
                    ? string.Empty
                    : $",\n        \"digest\": \"sha256:{DigestSha256Override}\"";
                string json = $$"""
                    [
                      {
                        "tag_name": "v0.9.5",
                        "name": "OptiScaler v0.9.5",
                        "draft": false,
                        "prerelease": false,
                        "published_at": "2026-11-16T12:00:00Z",
                        "html_url": "https://github.com/OptiScaler/OptiScaler/releases/tag/v0.9.5",
                        "assets": [
                          {
                            "name": "OptiScaler_v0.9.5.zip",
                            "browser_download_url": "https://github.com/OptiScaler/OptiScaler/releases/download/v0.9.5/OptiScaler_v0.9.5.zip",
                            "size": 204800{{digestField}}
                          },
                          {
                            "name": "DLSS-NR-on-AMD-v0.9.0.zip",
                            "browser_download_url": "https://github.com/danielblnc/DLSS-NR-on-AMD/releases/download/v0.9.0/DLSS-NR-on-AMD-v0.9.0.zip",
                            "size": 1048576
                          }
                        ]
                      }
                    ]
                    """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (Mode == DownloadMode.CorruptBytes)
            {
                byte[] bad = Encoding.UTF8.GetBytes("NOT_A_VALID_ZIP_ARCHIVE");
                var msg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bad) };
                msg.Content.Headers.ContentLength = bad.Length;
                return Task.FromResult(msg);
            }

            if (Mode == DownloadMode.PausingSlowStream)
            {
                StreamEntered = true;
                var msg = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new PausedChunkStream(_pauseGate))
                };
                msg.Content.Headers.ContentLength = 262144;
                return Task.FromResult(msg);
            }

            var okMsg = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validZipBytes) };
            okMsg.Content.Headers.ContentLength = validZipBytes.Length;
            return Task.FromResult(okMsg);
        }

        private sealed class PausedChunkStream(TaskCompletionSource gate) : Stream
        {
            private int _step;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 262144;
            public override long Position { get => _step * 32768; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_step == 0)
                {
                    _step = 1;
                    int n = Math.Min(32768, buffer.Length);
                    buffer.Span[..n].Fill(0x50);
                    return n;
                }
                await gate.Task.WaitAsync(cancellationToken);
                return 0;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
