using System.IO.Compression;
using System.Net;
using System.Net.Http;
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
using ProjectLuma.UpdateService;
using Xunit;

namespace ProjectLuma.Tests;

public sealed class InstallerAndPackageTests
{
    [Fact]
    public void IniMerger_Preserves_Comments_And_Custom_User_Keys()
    {
        string original = """
            ; User custom comment
            [Upscalers]
            Dx12Upscaler=fsr22
            MyCustomSetting=keep_me

            [Overlay]
            ShowFps=true
            """;

        var merge = new Dictionary<string, Dictionary<string, string>>
        {
            ["Upscalers"] = new() { ["Dx12Upscaler"] = "ffx", ["Dx11Upscaler"] = "fsr31" },
            ["FSR4"] = new() { ["Fsr4ForceEnableInt8"] = "auto" }
        };

        string result = IniMerger.MergeSections(original, merge);

        Assert.Contains("; User custom comment", result);
        Assert.Contains("MyCustomSetting=keep_me", result);
        Assert.Contains("Dx12Upscaler=ffx", result);
        Assert.Contains("Dx11Upscaler=fsr31", result);
        Assert.Contains("[FSR4]", result);
        Assert.Contains("Fsr4ForceEnableInt8=auto", result);
    }

    [Fact]
    public async Task TransactionalGameInstaller_BacksUp_DetectsDrift_PreservesUserMods_And_RestoresByteExact()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaInstallTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string gameDir = Path.Combine(tempRoot, "MockDx12Game");
            string stageDir = Path.Combine(tempRoot, "StagedOptiScaler");
            string backupVault = Path.Combine(tempRoot, "BackupVault");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(stageDir);

            // Use a real 64-bit Windows executable so PE validation passes honestly
            string mockExe = Path.Combine(gameDir, "MockGame.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), mockExe);

            // Pre-existing original dxgi.dll in the game folder that must be backed up and restored byte-for-byte
            string origDxgi = Path.Combine(gameDir, "dxgi.dll");
            byte[] origBytes = "ORIGINAL_GAME_DXGI_BINARY_V1"u8.ToArray();
            await File.WriteAllBytesAsync(origDxgi, origBytes);

            // Staged OptiScaler payload
            await File.WriteAllBytesAsync(Path.Combine(stageDir, "OptiScaler.dll"), "OPTISCALER_V095_PAYLOAD"u8.ToArray());

            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, backupVault);

            var game = new DiscoveredGame
            {
                Id = "mock-1",
                Title = "Mock DX12 Game",
                ExecutablePath = mockExe,
                ExecutableDirectory = gameDir,
                InstallDirectory = gameDir,
                GraphicsApi = GraphicsApi.DirectX12,
                Is64Bit = true
            };

            var profile = new GameEnhancementProfile
            {
                GameId = game.Id,
                GameTitle = game.Title,
                EnableSuperResolution = true,
                SelectedUpscalerEngine = "FSR 4.1.2 INT8 (FSR4-i8-2)",
                SelectedUpscalingPreset = UpscalingQualityPreset.Quality,
                ProxyDllName = "dxgi.dll",
                TargetBackendVersion = "v0.9.5"
            };

            var manifests = new[]
            {
                new BackendManifest { Id = "optiscaler", CurrentInstalledVersion = "v0.9.5", StagedInstallPath = stageDir }
            };

            var orchestrator = new BackendOrchestrator();
            var plan = orchestrator.ComposeCombinedPlan(game, profile, manifests, Array.Empty<UserImportedDllRecord>());

            var session = await installer.InstallOrUpdateAsync(game, profile, plan);
            Assert.True(session.Completed);
            Assert.Equal(IntegrationHealthState.Healthy, await installer.VerifyInstallationHealthAsync(game));
            Assert.True(File.Exists(Path.Combine(gameDir, "OptiScaler.ini")));

            // User places an unrelated third-party mod file afterward -> must NOT be touched on uninstall
            string userModFile = Path.Combine(gameDir, "reshade_custom_lut.png");
            await File.WriteAllTextAsync(userModFile, "USER_MOD_DATA");

            // Simulate a Steam game update modifying OptiScaler.ini -> VerifyInstallationHealthAsync must detect Drift
            await File.AppendAllTextAsync(Path.Combine(gameDir, "OptiScaler.ini"), "\n; Steam patch modified\n");
            Assert.Equal(IntegrationHealthState.DriftDetected, await installer.VerifyInstallationHealthAsync(game));

            // Uninstall: restores original dxgi.dll byte-for-byte, preserves user-modified INI & custom mod file
            bool uninstalled = await installer.UninstallAsync(game);
            Assert.True(uninstalled);
            Assert.Equal(origBytes, await File.ReadAllBytesAsync(origDxgi));
            Assert.True(File.Exists(userModFile));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void PackageManager_Blocks_Untrusted_Endpoints_And_Allows_Official_GitHub()
    {
        Assert.True(GitHubPackageManager.IsAllowedEndpoint("https://api.github.com/repos/OptiScaler/OptiScaler/releases"));
        Assert.True(GitHubPackageManager.IsAllowedEndpoint("https://objects.githubusercontent.com/some/asset.7z"));
        Assert.False(GitHubPackageManager.IsAllowedEndpoint("http://github.com/OptiScaler/OptiScaler"));
        Assert.False(GitHubPackageManager.IsAllowedEndpoint("https://random-mod-mirror.example.com/payload.exe"));
    }

    [Fact]
    public async Task UserDllVault_Rejects_Disallowed_Or_Invalid_Dll_And_Accepts_Valid_64Bit_Dll()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaVaultTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "test.db"));
            await repo.InitializeAsync();
            var vault = new UserDllVault(repo, logger, Path.Combine(tempRoot, "vault"));

            // Reject disallowed filename
            string badFile = Path.Combine(tempRoot, "random.dll");
            await File.WriteAllBytesAsync(badFile, new byte[] { 1, 2, 3 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.ImportDllAsync(badFile));

            // Reject fake non-PE nvngx_dlssnr.dll
            string fakeNr = Path.Combine(tempRoot, "nvngx_dlssnr.dll");
            await File.WriteAllTextAsync(fakeNr, "NOT_A_PE_BINARY");
            await Assert.ThrowsAsync<InvalidOperationException>(() => vault.ImportDllAsync(fakeNr));

            // Accept genuine 64-bit Windows DLL copied as nvngx_dlss.dll (note: system DLL has PE32+ x64 DLL header)
            string sysDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll");
            string validImportDir = Path.Combine(tempRoot, "valid");
            Directory.CreateDirectory(validImportDir);
            string testDlss = Path.Combine(validImportDir, "nvngx_dlss.dll");
            File.Copy(sysDll, testDlss);

            var rec = await vault.ImportDllAsync(testDlss);
            Assert.True(rec.IsPe64Valid);
            Assert.Equal("nvngx_dlss", rec.DllKind);
            Assert.Equal(64, rec.Sha256.Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task PackageManager_Stages_Local_Zip_And_RollsBack_Previous_Version()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaStageTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "test.db"));
            await repo.InitializeAsync();
            var pm = new GitHubPackageManager(repo, logger, stagingRootDirectory: Path.Combine(tempRoot, "staging"));

            string zipV1 = Path.Combine(tempRoot, "opti_v1.zip");
            string zipV2 = Path.Combine(tempRoot, "opti_v2.zip");
            CreateDummyZip(zipV1, "OptiScaler.dll", "V1_BINARY");
            CreateDummyZip(zipV2, "OptiScaler.dll", "V2_BINARY");

            var manifests = await pm.GetManifestsAsync();
            var opti = manifests.First(m => m.Id == "optiscaler");

            await pm.StageLocalArchiveAsync(opti, zipV1, "v0.9.4");
            Assert.Equal("v0.9.4", opti.CurrentInstalledVersion);
            Assert.True(await pm.VerifyBackendIntegrityAsync("optiscaler"));

            await pm.StageLocalArchiveAsync(opti, zipV2, "v0.9.5");
            Assert.Equal("v0.9.5", opti.CurrentInstalledVersion);

            bool rolledBack = await pm.RollbackBackendAsync("optiscaler");
            Assert.True(rolledBack);
            var reloaded = (await pm.GetManifestsAsync()).First(m => m.Id == "optiscaler");
            Assert.Equal("v0.9.4", reloaded.CurrentInstalledVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public async Task UpdateCoordinator_Handles_Network_Failure_Gracefully_In_Offline_Mode()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaOfflineTest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var logger = new LumaLogger(Path.Combine(tempRoot, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(tempRoot, "test.db"));
            await repo.InitializeAsync();

            using var failingClient = new HttpClient(new FailingHttpHandler());
            var pm = new GitHubPackageManager(repo, logger, failingClient, Path.Combine(tempRoot, "staging"));
            var gpuDetector = new WindowsGpuDetector(logger);
            var orchestrator = new BackendOrchestrator();
            var installer = new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "backups"));

            var coordinator = new UpdateCoordinatorService(gpuDetector, repo, pm, orchestrator, installer, logger);
            await coordinator.CheckNowAsync();

            Assert.True(coordinator.Status.IsOffline);
            Assert.False(coordinator.Status.IsChecking);
            Assert.NotEmpty(coordinator.Status.Items);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static void CreateDummyZip(string zipPath, string entryName, string content)
    {
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated offline network failure", null, HttpStatusCode.ServiceUnavailable);
    }
}
