using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ProjectLuma.BackendManager;
using ProjectLuma.Compatibility;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using ProjectLuma.GameDiscovery;
using ProjectLuma.GameInstaller;
using ProjectLuma.Logging;
using ProjectLuma.PackageManager;
using ProjectLuma.UpdateService;
using Xunit;

namespace ProjectLuma.Tests;

public sealed class NeuralHubAndLiveUpdateTests
{
    private static DetectedGpuInfo CreateRx6600(bool hip72 = false) => new()
    {
        Manufacturer = "AMD",
        ModelName = "AMD Radeon RX 6600",
        VendorId = 0x1002,
        DeviceId = 0x73FF,
        Architecture = GpuArchitecture.RDNA2,
        GpuTarget = "gfx1032",
        VramBytes = 8541929472UL,
        DriverVersion = "32.0.21045.29003",
        AdrenalinVersionDisplay = "26.11.2",
        MeetsAdrenalin2611Minimum = true,
        DirectX12Supported = true,
        HipRuntimeInstalled = true,
        HipRuntimeDllName = hip72 ? "amdhip64_7.dll" : "amdhip64_6.dll",
        HipRuntimeVersion = hip72 ? "7.2.0" : "10.0.3652.0",
        HipRuntimeApiVersion = 60450101,
        HipRuntimeInitialized = true,
        Hip72SdkAvailable = hip72
    };

    private static string CreateMockNeuralHubStore(
        string tempRoot,
        out string nrDllSha256,
        out string brightMemoryDir,
        out string aniimoDir)
    {
        string storeDir = Path.Combine(tempRoot, "Danielblnc", "NeuralHub");
        string rtDir = Path.Combine(storeDir, "runtime", "v0.9.0");
        string ffxDir = Path.Combine(rtDir, "ffx");
        string dataDir = Path.Combine(storeDir, "data");
        Directory.CreateDirectory(ffxDir);
        Directory.CreateDirectory(dataDir);

        File.WriteAllText(Path.Combine(storeDir, "neuralhub.exe"), "MZ_NEURALHUB_0_9_0");
        File.WriteAllText(Path.Combine(rtDir, ".nh_payload"), "nh_v0.9.0");
        File.WriteAllText(Path.Combine(rtDir, "neuralhub_host.dll"), "MZ_HOST");
        File.WriteAllText(Path.Combine(ffxDir, "amd_fidelityfx_upscaler_dx12.dll"), "MZ_FFX_UP");
        File.WriteAllText(Path.Combine(ffxDir, "amd_fidelityfx_framegeneration_dx12.dll"), "MZ_FFX_FG");

        byte[] dllBytes = Encoding.UTF8.GetBytes("MOCK_NVIDIA_DLSSNR_310_9_16_0");
        nrDllSha256 = Convert.ToHexString(SHA256.HashData(dllBytes));
        string nrFileName = $"nr-{nrDllSha256.ToLowerInvariant()}.dll";
        File.WriteAllBytes(Path.Combine(dataDir, nrFileName), dllBytes);

        File.WriteAllText(Path.Combine(dataDir, "index.ini"),
            $"; NeuralHub data index\n[nr]\nFile={nrFileName}\nSha256={nrDllSha256.ToLowerInvariant()}\nFileVersion=310.9.16.0\nModels=HT, CC\n");

        // Game 1: Bright Memory style — NeuralHub stub + config + runtime log
        brightMemoryDir = Path.Combine(tempRoot, "Games", "BrightMemoryInfinite", "Binaries", "Win64");
        Directory.CreateDirectory(brightMemoryDir);
        File.WriteAllText(Path.Combine(brightMemoryDir, "BrightMemoryInfinite-Win64-Shipping.exe"), "MZ");
        File.WriteAllBytes(Path.Combine(brightMemoryDir, "winmm.dll"), new byte[96_768]);
        File.WriteAllText(Path.Combine(brightMemoryDir, "neuralhub.ini"), "[NeuralHub]\nProxy=winmm.dll\n");
        File.WriteAllText(Path.Combine(brightMemoryDir, "dlssnr_on_amd.log"), "[dlssnr] initialized ok\n");

        // Game 2: Aniimo style — OptiScaler proxy conflict (large version.dll + OptiScaler.ini + D3D12_Optiscaler)
        aniimoDir = Path.Combine(tempRoot, "Games", "Aniimo");
        Directory.CreateDirectory(Path.Combine(aniimoDir, "D3D12_Optiscaler"));
        File.WriteAllText(Path.Combine(aniimoDir, "Aniimo.exe"), "MZ");
        File.WriteAllBytes(Path.Combine(aniimoDir, "version.dll"), new byte[3_000_000]);
        File.WriteAllText(Path.Combine(aniimoDir, "OptiScaler.ini"), "[Upscalers]\nDx12Upscaler=fsr4\n");

        var gamesPayload = new
        {
            games = new object[]
            {
                new
                {
                    id = "brightmemoryinfinite_win64_shipp-1d760161",
                    name = "BrightMemoryInfinite",
                    dir = brightMemoryDir,
                    exe = "BrightMemoryInfinite-Win64-Shipping.exe",
                    proxy = "winmm.dll",
                    key = $"manual:{brightMemoryDir.ToLowerInvariant()}"
                },
                new
                {
                    id = "aniimo-25807e6c",
                    name = "Aniimo",
                    dir = aniimoDir,
                    exe = "",
                    proxy = "version.dll",
                    key = "steam:4126040",
                    over_dir = aniimoDir
                }
            }
        };
        File.WriteAllText(Path.Combine(storeDir, "games.json"), JsonSerializer.Serialize(gamesPayload));
        return storeDir;
    }

    [Fact]
    public void NeuralHubDetector_InspectsAll7States_VaultSync_And_OptiScalerConflicts_ReadOnly()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaNhTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string storeDir = CreateMockNeuralHubStore(tempRoot, out string nrSha256, out string bmDir, out string aniimoDir);
            string rtDir = Path.Combine(storeDir, "runtime", "v0.9.0");
            var options = new NeuralHubProbeOptions
            {
                DisableHostProbe = true,
                ExplicitRegistryStorePath = storeDir,
                ExplicitRegistryRuntimePath = rtDir,
                ExplicitRegistryRuntimeVersion = "v0.9.0",
                ExplicitRegistryRuntimeAbi = "1"
            };

            string vaultDllPath = Path.Combine(tempRoot, "Vault", "nvngx_dlssnr.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(vaultDllPath)!);
            File.WriteAllBytes(vaultDllPath, File.ReadAllBytes(Path.Combine(storeDir, "data", $"nr-{nrSha256.ToLowerInvariant()}.dll")));

            var vaultRec = new UserImportedDllRecord
            {
                FileName = "nvngx_dlssnr.dll",
                DllKind = "nvngx_dlssnr",
                FileVersion = "310.9.16.0",
                Sha256 = nrSha256,
                StoredPath = vaultDllPath
            };

            var games = new[]
            {
                new DiscoveredGame { Id = "bm", Title = "Bright Memory: Infinite", ExecutableDirectory = bmDir, ExecutablePath = Path.Combine(bmDir, "BrightMemoryInfinite-Win64-Shipping.exe") },
                new DiscoveredGame { Id = "aniimo", Title = "Aniimo", InstallDirectory = aniimoDir, ExecutableDirectory = aniimoDir, ExecutablePath = Path.Combine(aniimoDir, "Aniimo.exe"), Launcher = GameLauncherType.Steam, StoreAppId = "4126040" }
            };

            var report = NeuralHubDetector.InspectFullStatus(null, vaultRec, games, options);

            // 1. App + Runtime verified
            Assert.True(report.IsAppDetected);
            Assert.True(report.IsRuntimeVerified);
            Assert.True(report.HasPayloadMarker);
            Assert.True(report.HasHostDll);
            Assert.True(report.HasBundledFsrUpscaler);
            Assert.True(report.HasBundledFsrFrameGen);
            Assert.Contains("Installed and detected", report.StatusHeadline);

            // 2. NeuralHub DLL + Project Luma Vault SHA-256 synchronization (zero duplication)
            Assert.True(report.IsDllImportedInNeuralHub);
            Assert.Equal("310.9.16.0", report.NeuralHubDllVersion);
            Assert.Equal("HT, CC", report.NeuralHubSupportedModels);
            Assert.True(report.IsDllImportedInLumaVault);
            Assert.True(report.IsVaultSynchronizedWithNeuralHub);
            Assert.Contains("SHA-256 verified match", report.DllSyncSummary);

            // 3. Per-game Bright Memory verified vs Aniimo OptiScaler conflict preserved safely
            var bmStatus = NeuralHubDetector.InspectGameIntegration(games[0], report);
            Assert.True(bmStatus.IsRegisteredInNeuralHub);
            Assert.True(bmStatus.IsGameLoaderInstalled);
            Assert.True(bmStatus.IsGameConfigured);
            Assert.True(bmStatus.HasRuntimeLogInitialized);
            Assert.False(bmStatus.HasOptiScalerProxyConflict);

            var aniimoStatus = NeuralHubDetector.InspectGameIntegration(games[1], report);
            Assert.True(aniimoStatus.IsRegisteredInNeuralHub);
            Assert.True(aniimoStatus.HasOptiScalerProxyConflict);
            Assert.False(aniimoStatus.IsGameLoaderInstalled);
            Assert.Contains("OptiScaler", aniimoStatus.ConflictDetail);
            Assert.True(File.Exists(Path.Combine(aniimoDir, "OptiScaler.ini")));
            Assert.Equal(3_000_000, new FileInfo(Path.Combine(aniimoDir, "version.dll")).Length);

            // 4. Legacy Detect() agrees
            var simple = NeuralHubDetector.Detect(null, options);
            Assert.True(simple.IsDetected);
            Assert.EndsWith("neuralhub.exe", simple.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task PackageManager_DetectsExternalNeuralHub_RecognizesDllWithoutVaultDuplicate_And_NeverDeletesExternalDir()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaNhPm_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string storeDir = CreateMockNeuralHubStore(tempRoot, out _, out _, out _);
            string rtDir = Path.Combine(storeDir, "runtime", "v0.9.0");
            var options = new NeuralHubProbeOptions
            {
                DisableHostProbe = true,
                ExplicitRegistryStorePath = storeDir,
                ExplicitRegistryRuntimePath = rtDir,
                ExplicitRegistryRuntimeVersion = "v0.9.0"
            };

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "luma.db"));
            await repo.InitializeAsync();

            var pkgMgr = new GitHubPackageManager(repo, logger, stagingRootDirectory: Path.Combine(tempRoot, "backends"), neuralHubProbeOptions: options);
            var manifests = await pkgMgr.GetManifestsAsync();
            var nr = manifests.First(m => m.Id == "dlss-nr-on-amd");
            Assert.Equal("v0.9.0", nr.CurrentInstalledVersion);
            Assert.Equal(rtDir, nr.StagedInstallPath);

            Assert.True(await pkgMgr.VerifyBackendIntegrityAsync("dlss-nr-on-amd"));

            // RemoveBackendAsync must NEVER delete the external NeuralHub directory!
            await pkgMgr.RemoveBackendAsync("dlss-nr-on-amd");
            Assert.True(Directory.Exists(rtDir));
            Assert.True(File.Exists(Path.Combine(rtDir, "neuralhub_host.dll")));

            // Without Vault copy, CapabilityEngine + IntelligentComponentResolver still recognize NeuralHub's imported DLL
            var vault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));
            var orch = new BackendOrchestrator();
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "backups"));
            var emptyDl = Path.Combine(tempRoot, "empty_dl");
            Directory.CreateDirectory(emptyDl);
            var resolver = new IntelligentComponentResolver(pkgMgr, vault, repo, orch, installer, logger, emptyDl, options);
            var gpu = CreateRx6600(hip72: true);
            var engine = new CapabilityEngine(options);
            var game = new DiscoveredGame
            {
                Id = "g1",
                Title = "DX12 Title",
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true,
                NativeDlssSupported = true,
                ExecutableDirectory = tempRoot,
                InstallDirectory = tempRoot
            };
            var profile = engine.EvaluateGame(game, gpu, Array.Empty<UserImportedDllRecord>(), manifests, null, UpdateChannel.Stable);
            profile.EnableNeuralRendering = true;
            Assert.Equal(NeuralRenderingActivationState.ReadyToConfigure, profile.NeuralActivationState);

            var plan = await resolver.AnalyzeRequirementsAsync(game, profile, gpu);
            Assert.Contains(plan.Components, c => c.ComponentId == "dlss-nr-on-amd" && c.IsSatisfied);
            Assert.Contains(plan.Components, c => c.ComponentId == "nvngx_dlssnr" && c.IsSatisfied);
            Assert.Empty(await vault.GetImportedDllsAsync()); // No duplication into Vault
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task AppSelfUpdateManager_HandlesSuccess_UpToDate_Offline_429_BadHash_CorruptZip_ZipSlip_DisallowedHost_And_Cancel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaSelfUpd_" + Guid.NewGuid().ToString("N")[..8]);
        string oldEnv = Environment.GetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar) ?? string.Empty;
        try
        {
            string isolatedAppData = Path.Combine(tempRoot, "AppData");
            Environment.SetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar, isolatedAppData);

            // Build valid v3.3.1 release zip bytes containing ProjectLuma.exe and ProjectLuma.Updater.exe
            byte[] validZipBytes = CreateZipBytes(new Dictionary<string, byte[]>
            {
                ["ProjectLuma.exe"] = Encoding.UTF8.GetBytes("MOCK_LUMA_3_3_1"),
                ["ProjectLuma.Updater.exe"] = Encoding.UTF8.GetBytes("MOCK_UPDATER_3_3_1")
            });
            string validSha = Convert.ToHexString(SHA256.HashData(validZipBytes)).ToLowerInvariant();
            string shaSums = $"{validSha}  ProjectLuma-v3.3.1-win-x64.zip\n";

            string releaseJson = CreateReleasesJson("v3.3.1", "ProjectLuma-v3.3.1-win-x64.zip",
                "https://github.com/Subho-XD/Project-Luma/releases/download/v3.3.1/ProjectLuma-v3.3.1-win-x64.zip",
                validSha,
                "https://github.com/Subho-XD/Project-Luma/releases/download/v3.3.1/SHA256SUMS.txt");

            var logger = new LumaLogger(Path.Combine(isolatedAppData, "logs"));
            var repo = new SqliteCompatibilityRepository(logger);
            await repo.InitializeAsync();
            var pkgMgr = new GitHubPackageManager(repo, logger, new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }))), neuralHubProbeOptions: NeuralHubProbeOptions.Disabled);
            var coordinator = new UpdateCoordinatorService(new StubGpuDetector(CreateRx6600()), repo, pkgMgr, new BackendOrchestrator(), new TransactionalGameInstaller(new PeAnalyzer(), logger), logger);
            coordinator.ConfigureNeuralHubProbe(NeuralHubProbeOptions.Disabled);

            // 1. Update available (3.3.0 -> 3.3.1) + stage verified
            using var okHttp = new HttpClient(new StubHandler((req, _) =>
            {
                string u = req.RequestUri!.ToString();
                if (u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var zipResp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validZipBytes) };
                    zipResp.Content.Headers.ContentLength = validZipBytes.Length;
                    return Task.FromResult(zipResp);
                }
                if (u.Contains("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(shaSums) });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8, "application/json") });
            }));

            string mockInstallDir = Path.Combine(tempRoot, "InstalledApp");
            Directory.CreateDirectory(mockInstallDir);
            coordinator.ConfigureAppSelfUpdater(okHttp, AppSelfUpdateManager.DefaultReleasesApiUrl, mockInstallDir, "3.3.0");
            await coordinator.CheckNowAsync();

            var appItem = coordinator.Status.Items.First(i => i.ComponentId == "project-luma-app");
            Assert.Equal("Update available", appItem.StateBadgeText);
            Assert.Equal("Update Project Luma", appItem.ActionLabel);
            Assert.True(appItem.CanApply);

            var stage = await coordinator.DownloadAndStageAppUpdateAsync(appItem);
            Assert.True(stage.Succeeded, stage.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(stage.StageDirectory, "ProjectLuma.exe")));
            Assert.True(File.Exists(Path.Combine(stage.StageDirectory, "ProjectLuma.Updater.exe")));
            Assert.True(File.Exists(stage.UpdaterExecutablePath));
            Assert.DoesNotContain(mockInstallDir, stage.UpdaterExecutablePath, StringComparison.OrdinalIgnoreCase);

            // 2. Already up to date (3.3.1 == 3.3.1)
            coordinator.ConfigureAppSelfUpdater(okHttp, AppSelfUpdateManager.DefaultReleasesApiUrl, mockInstallDir, "3.3.1");
            await coordinator.CheckNowAsync();
            var upToDateItem = coordinator.Status.Items.First(i => i.ComponentId == "project-luma-app");
            Assert.Equal("Installed", upToDateItem.StateBadgeText);
            Assert.False(upToDateItem.ShowActionButton);

            // 3. Offline check
            using var offlineHttp = new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("Simulated network down")));
            var mgrOffline = new AppSelfUpdateManager(logger, offlineHttp, currentInstallDirectory: mockInstallDir, appVersionOverride: "3.3.0");
            var offlineItem = await mgrOffline.CheckForAppUpdateAsync("3.3.0", UpdateChannel.Stable, CancellationToken.None);
            Assert.Equal("Offline", offlineItem.StateBadgeText);
            Assert.Equal("Retry Check", offlineItem.ActionLabel);

            // 4. Rate limit HTTP 429
            using var rateHttp = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)429))));
            var mgrRate = new AppSelfUpdateManager(logger, rateHttp, currentInstallDirectory: mockInstallDir, appVersionOverride: "3.3.0");
            var rateItem = await mgrRate.CheckForAppUpdateAsync("3.3.0", UpdateChannel.Stable, CancellationToken.None);
            Assert.Equal("Rate limited", rateItem.StateBadgeText);
            Assert.Equal("Retry Check", rateItem.ActionLabel);

            // 5. SHA-256 mismatch
            var badHashItem = CloneItem(appItem, expectedSha256: new string('a', 64), assetName: "badhash.zip");
            var badHashRes = await new AppSelfUpdateManager(logger, okHttp, currentInstallDirectory: mockInstallDir).DownloadAndStageAsync(badHashItem, CancellationToken.None);
            Assert.False(badHashRes.Succeeded);
            Assert.Contains("SHA-256 mismatch", badHashRes.ErrorMessage);
            Assert.False(File.Exists(Path.Combine(isolatedAppData, "self_update", "downloads", "badhash.zip")));

            // 6. Corrupted zip (valid SHA-256 of invalid zip payload)
            byte[] corruptBytes = Encoding.UTF8.GetBytes("NOT_A_VALID_ZIP_FILE");
            string corruptSha = Convert.ToHexString(SHA256.HashData(corruptBytes)).ToLowerInvariant();
            using var corruptHttp = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(corruptBytes) })));
            var corruptItem = CloneItem(appItem, expectedSha256: corruptSha, assetName: "corrupt.zip");
            var corruptRes = await new AppSelfUpdateManager(logger, corruptHttp, currentInstallDirectory: mockInstallDir).DownloadAndStageAsync(corruptItem, CancellationToken.None);
            Assert.False(corruptRes.Succeeded);
            Assert.Contains("corrupted", corruptRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // 7. Zip-Slip path traversal entry rejected
            byte[] slipZip = CreateZipBytes(new Dictionary<string, byte[]> { ["../../evil.exe"] = Encoding.UTF8.GetBytes("EVIL") });
            string slipSha = Convert.ToHexString(SHA256.HashData(slipZip)).ToLowerInvariant();
            using var slipHttp = new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(slipZip) })));
            var slipItem = CloneItem(appItem, expectedSha256: slipSha, assetName: "slip.zip");
            var slipRes = await new AppSelfUpdateManager(logger, slipHttp, currentInstallDirectory: mockInstallDir).DownloadAndStageAsync(slipItem, CancellationToken.None);
            Assert.False(slipRes.Succeeded);
            Assert.Contains("Zip-Slip", slipRes.ErrorMessage);

            // 8. Disallowed host rejected
            var badHostItem = CloneItem(appItem, downloadUrl: "https://untrusted.example.com/ProjectLuma-v3.3.1-win-x64.zip");
            var badHostRes = await new AppSelfUpdateManager(logger, okHttp, currentInstallDirectory: mockInstallDir).DownloadAndStageAsync(badHostItem, CancellationToken.None);
            Assert.False(badHostRes.Succeeded);
            Assert.Contains("allowlist", badHostRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // 9. Cancellation cleans up .partial
            using var cancelCts = new CancellationTokenSource();
            using var slowHttp = new HttpClient(new StubHandler((_, token) =>
            {
                cancelCts.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }));
            var cancelItem = CloneItem(appItem, assetName: "cancel.zip");
            var cancelRes = await new AppSelfUpdateManager(logger, slowHttp, currentInstallDirectory: mockInstallDir).DownloadAndStageAsync(cancelItem, cancelCts.Token);
            Assert.False(cancelRes.Succeeded);
            Assert.Contains("canceled", cancelRes.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(isolatedAppData, "self_update", "downloads", "cancel.zip.partial")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar, string.IsNullOrEmpty(oldEnv) ? null : oldEnv);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void Updater_ApplyUpdateTransactional_RollsBackCleanly_WhenHealthCheckFails()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaUpdaterRb_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string stageDir = Path.Combine(tempRoot, "stage");
            string targetDir = Path.Combine(tempRoot, "target");
            Directory.CreateDirectory(stageDir);
            Directory.CreateDirectory(targetDir);

            // Copy real compiled ProjectLuma.Updater.exe into stage as fake failing ProjectLuma.exe (--health-check returns non-zero on Updater.exe)
            string updaterBin = Path.Combine(AppContext.BaseDirectory, "ProjectLuma.Updater.exe");
            Assert.True(File.Exists(updaterBin), $"Expected {updaterBin} in test bin output");

            File.WriteAllText(Path.Combine(targetDir, "ProjectLuma.exe"), "ORIGINAL_V3_3_0");
            File.WriteAllText(Path.Combine(targetDir, "preserve_user.txt"), "USER_SETTINGS_SAFE");
            File.Copy(updaterBin, Path.Combine(stageDir, "ProjectLuma.exe"));
            File.WriteAllText(Path.Combine(stageDir, "new_v331_only.txt"), "SHOULD_ROLL_BACK");

            Assert.Throws<InvalidOperationException>(() =>
                ProjectLuma.Updater.Program.ApplyUpdateTransactional(
                    stageDir,
                    targetDir,
                    exeToHealthCheck: Path.Combine(targetDir, "ProjectLuma.exe")));

            Assert.Equal("ORIGINAL_V3_3_0", File.ReadAllText(Path.Combine(targetDir, "ProjectLuma.exe")));
            Assert.Equal("USER_SETTINGS_SAFE", File.ReadAllText(Path.Combine(targetDir, "preserve_user.txt")));
            Assert.False(File.Exists(Path.Combine(targetDir, "new_v331_only.txt")));
            Assert.False(Directory.Exists(Path.Combine(targetDir, ".updater_rollback")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static UpdateCheckItem CloneItem(
        UpdateCheckItem src,
        string? downloadUrl = null,
        string? expectedSha256 = null,
        string? assetName = null) => new()
    {
        ComponentId = src.ComponentId,
        ComponentName = src.ComponentName,
        Kind = src.Kind,
        InstalledVersion = src.InstalledVersion,
        AvailableVersion = src.AvailableVersion,
        DownloadUrl = downloadUrl ?? src.DownloadUrl,
        ExpectedSha256 = expectedSha256 ?? src.ExpectedSha256,
        AssetName = assetName ?? src.AssetName
    };

    private static byte[] CreateZipBytes(Dictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var kv in files)
            {
                var entry = zip.CreateEntry(kv.Key);
                using var s = entry.Open();
                s.Write(kv.Value, 0, kv.Value.Length);
            }
        }
        return ms.ToArray();
    }

    private static string CreateReleasesJson(string tag, string zipName, string zipUrl, string zipSha, string sumsUrl) =>
        JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = tag,
                name = $"Project Luma {tag}",
                draft = false,
                prerelease = false,
                html_url = $"https://github.com/Subho-XD/Project-Luma/releases/tag/{tag}",
                assets = new[]
                {
                    new { name = zipName, browser_download_url = zipUrl, digest = $"sha256:{zipSha}" },
                    new { name = "SHA256SUMS.txt", browser_download_url = sumsUrl, digest = string.Empty }
                }
            }
        });

    private sealed class StubGpuDetector(DetectedGpuInfo gpu) : IGpuDetector
    {
        public Task<DetectedGpuInfo> DetectPrimaryGpuAsync(CancellationToken ct = default) => Task.FromResult(gpu);
        public IReadOnlyList<GpuCatalogEntry> GetCatalogEntries() => Array.Empty<GpuCatalogEntry>();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => fn(request, cancellationToken);
    }
}
