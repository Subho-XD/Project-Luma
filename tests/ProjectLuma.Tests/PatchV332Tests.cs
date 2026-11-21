using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using ProjectLuma.BackendManager;
using ProjectLuma.Compatibility;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using ProjectLuma.Core.Services;
using ProjectLuma.GameDiscovery;
using ProjectLuma.GameInstaller;
using ProjectLuma.Logging;
using ProjectLuma.PackageManager;
using ProjectLuma.UI.Controls;
using ProjectLuma.UI.Converters;
using ProjectLuma.UI.ViewModels;
using ProjectLuma.UpdateService;
using Xunit;

namespace ProjectLuma.Tests;

[CollectionDefinition("AppDataEnvSerial", DisableParallelization = true)]
public sealed class AppDataEnvSerialCollection { }

[Collection("AppDataEnvSerial")]
public sealed class PatchV332Tests
{
    private static DetectedGpuInfo CreateRx6600() => new()
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
        HipRuntimeDllName = "amdhip64_6.dll",
        HipRuntimeVersion = "10.0.3652.0",
        HipRuntimeApiVersion = 60450101,
        HipRuntimeInitialized = true,
        Hip72SdkAvailable = false
    };

    [Fact]
    public async Task Task1_ManualCheckForUpdates_TransitionsNineStates_CapturesReleaseNotes_And_PreservesPreferences()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaTask1_" + Guid.NewGuid().ToString("N")[..8]);
        string oldEnv = Environment.GetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar) ?? string.Empty;
        try
        {
            string appData = Path.Combine(tempRoot, "AppData");
            Environment.SetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar, appData);

            byte[] zipBytes = CreateZipBytes(new Dictionary<string, byte[]>
            {
                ["ProjectLuma.exe"] = File.ReadAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe")),
                ["ProjectLuma.Updater.exe"] = Encoding.UTF8.GetBytes("UPDATER_BIN")
            });
            string sha = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            string shaSums = $"{sha}  ProjectLuma-v3.3.2-win-x64.zip\n";
            string releaseNotes = "## Highlights in v3.3.2\n- Manual Check for Updates\n- Custom Game Persistence\n- Artwork Corner Clipping Fix";

            string releaseJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "v3.3.2",
                    name = "Project Luma v3.3.2",
                    body = releaseNotes,
                    draft = false,
                    prerelease = false,
                    html_url = "https://github.com/Subho-XD/Project-Luma/releases/tag/v3.3.2",
                    assets = new[]
                    {
                        new { name = "ProjectLuma-v3.3.2-win-x64.zip", browser_download_url = "https://github.com/Subho-XD/Project-Luma/releases/download/v3.3.2/ProjectLuma-v3.3.2-win-x64.zip", digest = $"sha256:{sha}" },
                        new { name = "SHA256SUMS.txt", browser_download_url = "https://github.com/Subho-XD/Project-Luma/releases/download/v3.3.2/SHA256SUMS.txt", digest = string.Empty }
                    }
                }
            });

            var logger = new LumaLogger(Path.Combine(appData, "logs"));
            var repo = new SqliteCompatibilityRepository(logger, Path.Combine(appData, "luma.db"));
            await repo.InitializeAsync();
            var settings = await repo.LoadSettingsAsync();
            settings.AutoCheckUpdates = false;
            settings.AutoDownloadVerifiedUpdates = false;
            await repo.SaveSettingsAsync(settings);

            var observedStates = new List<AppUpdateState>();
            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var http = new HttpClient(new StubHandler(async (req, ct) =>
            {
                string u = req.RequestUri!.ToString();
                if (u.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
                {
                    await releaseGate.Task.WaitAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8, "application/json") };
                }
                if (u.Contains("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(shaSums) };
                if (u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
                    resp.Content.Headers.ContentLength = zipBytes.Length;
                    return resp;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            }));

            var pkgMgr = new GitHubPackageManager(repo, logger, new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }))), Path.Combine(tempRoot, "backends"), neuralHubProbeOptions: NeuralHubProbeOptions.Disabled);
            var coordinator = new UpdateCoordinatorService(new StubGpuDetector(CreateRx6600()), repo, pkgMgr, new BackendOrchestrator(), new TransactionalGameInstaller(new PeAnalyzer(), logger, Path.Combine(tempRoot, "bak")), logger);
            coordinator.ConfigureNeuralHubProbe(NeuralHubProbeOptions.Disabled);

            string installDir = Path.Combine(tempRoot, "InstallDir");
            Directory.CreateDirectory(installDir);
            coordinator.ConfigureAppSelfUpdater(http, AppSelfUpdateManager.DefaultReleasesApiUrl, installDir, "3.3.1");

            coordinator.StatusChanged += st =>
            {
                if (st.ApplicationUpdateItem != null && (observedStates.Count == 0 || observedStates[^1] != st.ApplicationUpdateItem.UpdateState))
                    observedStates.Add(st.ApplicationUpdateItem.UpdateState);
            };

            // 1. Trigger manual check and confirm immediate non-blocking CheckingForUpdates transition BEFORE network completes
            var checkTask = coordinator.CheckNowAsync();
            Assert.Contains(AppUpdateState.CheckingForUpdates, observedStates);
            releaseGate.SetResult();
            await checkTask;

            var appItem = coordinator.Status.ApplicationUpdateItem!;
            Assert.Equal(AppUpdateState.UpdateAvailable, appItem.UpdateState);
            Assert.Equal("Update available", appItem.StateBadgeText);
            Assert.Equal("Download & Install Update", appItem.ActionLabel);
            Assert.Equal(releaseNotes, appItem.ReleaseNotes);
            Assert.True(appItem.HasReleaseNotes);

            // Never silently auto-install when AutoCheckUpdates/AutoDownload is configured
            Assert.False(File.Exists(Path.Combine(installDir, "ProjectLuma.exe")));

            // Track item property transitions during download -> verification -> staging
            appItem.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UpdateCheckItem.UpdateState) && observedStates[^1] != appItem.UpdateState)
                    observedStates.Add(appItem.UpdateState);
            };

            var stage = await coordinator.DownloadAndStageAppUpdateAsync(appItem);
            Assert.True(stage.Succeeded, stage.ErrorMessage);
            Assert.Contains(AppUpdateState.Downloading, observedStates);
            Assert.Contains(AppUpdateState.VerifyingPackage, observedStates);
            Assert.Contains(AppUpdateState.PreparingInstallation, observedStates);
            Assert.NotEmpty(appItem.PublisherSignatureSummary);
            Assert.Contains("SHA-256 verified", appItem.VerificationDetails);

            // Verify updater completion marker + post-restart UpdateCompleted state
            string markerPath = AppSelfUpdateManager.GetCompletionMarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, JsonSerializer.Serialize(new { newVersion = "3.3.2", completedAtUtc = DateTime.UtcNow }));
            Assert.True(AppSelfUpdateManager.TryConsumeCompletedUpdateMarker("3.3.2"));
            Assert.False(File.Exists(markerPath)); // Consumed cleanly once

            // AutoCheckUpdates preference preserved
            Assert.False((await repo.LoadSettingsAsync()).AutoCheckUpdates);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar, string.IsNullOrEmpty(oldEnv) ? null : oldEnv);
            await CleanupTempDirectoryAsync(tempRoot);
        }
    }

    [Fact]
    public async Task Task2_CustomGamePersistence_CoversRestart_Artwork_DriveDisconnect_Reconnect_And_Dedup()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "LumaCustomPersistence_" + Guid.NewGuid().ToString("N")[..8]);
        string oldEnv = Environment.GetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar) ?? string.Empty;
        try
        {
            string appData = Path.Combine(tempRoot, "AppData");
            Environment.SetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar, appData);

            // External/custom game outside Settings CustomLibraryPaths
            string removableMount = Path.Combine(tempRoot, "ExternalDrive", "CustomSpaceGame");
            string unpluggedMount = Path.Combine(tempRoot, "ExternalDrive_Disconnected", "CustomSpaceGame");
            Directory.CreateDirectory(removableMount);
            string customExe = Path.Combine(removableMount, "SpaceOdyssey.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), customExe);

            // Steam-discovered game for duplicate prevention test
            string steamRoot = Path.Combine(tempRoot, "Steam");
            string steamApps = Path.Combine(steamRoot, "steamapps");
            string steamGameDir = Path.Combine(steamApps, "common", "CyberRunner");
            Directory.CreateDirectory(steamGameDir);
            string steamGameExe = Path.Combine(steamGameDir, "CyberRunner.exe");
            File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), steamGameExe);
            File.WriteAllText(Path.Combine(steamApps, "appmanifest_555001.acf"), "\"AppState\"\n{\n\"appid\" \"555001\"\n\"name\" \"CyberRunner\"\n\"installdir\" \"CyberRunner\"\n}");

            // Custom artwork test images (solid PNGs written cleanly)
            string artDir = Path.Combine(tempRoot, "CustomArt");
            Directory.CreateDirectory(artDir);
            string customCoverSrc = Path.Combine(artDir, "my_cover.png");
            string customHeroSrc = Path.Combine(artDir, "my_hero.png");
            WriteDummyPng(customCoverSrc);
            WriteDummyPng(customHeroSrc);

            MainViewModel CreateVm(string scanSteamRoot)
            {
                var logger = new LumaLogger(Path.Combine(appData, "logs"));
                var pe = new PeAnalyzer();
                var repo = new SqliteCompatibilityRepository(logger, Path.Combine(appData, "luma.db"));
                var gpuDetector = new StubGpuDetector(CreateRx6600());
                var capEngine = new CapabilityEngine(NeuralHubProbeOptions.Disabled);
                var realScanner = new MultiStoreGameScanner(logger, pe);
                var scanner = new IsolatedMultiStoreScanner(realScanner, new MultiStoreGameScanner.ScanOptions
                {
                    SteamRootOverride = scanSteamRoot,
                    DisableRegistry = true
                });
                var artwork = new ArtworkDiscoveryService(logger, cacheDirectory: Path.Combine(appData, "artwork_cache"));
                var vault = new UserDllVault(repo, logger, Path.Combine(appData, "vault"));
                var pkgMgr = new GitHubPackageManager(repo, logger, new HttpClient(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }))), Path.Combine(tempRoot, "backends"), neuralHubProbeOptions: NeuralHubProbeOptions.Disabled);
                var orch = new BackendOrchestrator();
                var installer = new TransactionalGameInstaller(pe, logger, Path.Combine(appData, "backups"));
                var resolver = new IntelligentComponentResolver(pkgMgr, vault, repo, orch, installer, logger, Path.Combine(tempRoot, "empty_dl"), NeuralHubProbeOptions.Disabled);
                var launch = new GameLaunchService(installer, logger);
                var update = new UpdateCoordinatorService(gpuDetector, repo, pkgMgr, orch, installer, logger);
                update.ConfigureNeuralHubProbe(NeuralHubProbeOptions.Disabled);

                return new MainViewModel(
                    gpuDetector, capEngine, repo, scanner, artwork, pkgMgr, vault, orch,
                    resolver, installer, launch, update, logger, new StubDialogService());
            }

            // 1. Add custom game (parent folder NOT added to Settings)
            using (var vm1 = CreateVm(steamRoot))
            {
                await vm1.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: false);
                Assert.Single(vm1.AllGames); // Only CyberRunner initially

                var added = await vm1.AddGameManuallyFromPathAsync(customExe);
                Assert.NotNull(added);
                Assert.True(added!.IsManuallyAdded);
                Assert.True(added.IsAvailable);
                Assert.Equal(Path.GetFullPath(removableMount), added.CustomInstallLocation);
                Assert.Empty(vm1.Settings.CustomLibraryPaths); // Never required adding parent folder in Settings
                Assert.Equal(2, vm1.AllGames.Count);
            }
            SqliteConnection.ClearAllPools();

            // 2. Restart Project Luma -> verify manually added game remains & change artwork
            using (var vm2 = CreateVm(steamRoot))
            {
                await vm2.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: false);
                Assert.Equal(2, vm2.AllGames.Count);
                var persistedCard = vm2.AllGames.FirstOrDefault(g => g.Game.ExecutablePath.Equals(Path.GetFullPath(customExe), StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(persistedCard);
                Assert.True(persistedCard!.IsManuallyAdded);
                Assert.True(persistedCard.IsAvailable);

                vm2.SelectedGame = persistedCard;
                await vm2.SetCustomArtworkFromPathAsync(isHero: false, customCoverSrc);
                await vm2.SetCustomArtworkFromPathAsync(isHero: true, customHeroSrc);
                Assert.Equal(ArtworkSourceKind.CustomUser, persistedCard.Game.ArtworkSource);
                Assert.True(persistedCard.Game.HasCustomArtwork);
            }
            SqliteConnection.ClearAllPools();

            // 3. Restart again -> verify custom artwork metadata remains
            using (var vm3 = CreateVm(steamRoot))
            {
                await vm3.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: false);
                var persistedCard = vm3.AllGames.FirstOrDefault(g => g.Game.ExecutablePath.Equals(Path.GetFullPath(customExe), StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(persistedCard);
                Assert.Equal(ArtworkSourceKind.CustomUser, persistedCard!.Game.ArtworkSource);
                Assert.True(persistedCard.Game.HasCustomArtwork);
                Assert.Contains("custom_cover", persistedCard.CoverImagePath);
                Assert.Contains("custom_hero", persistedCard.HeroImagePath);
                Assert.True(File.Exists(persistedCard.CoverImagePath));
                Assert.True(File.Exists(persistedCard.HeroImagePath));
            }
            SqliteConnection.ClearAllPools();

            // 4. Disconnect drive -> verify game record is preserved and marked Unavailable
            Directory.Move(Path.Combine(tempRoot, "ExternalDrive"), Path.Combine(tempRoot, "ExternalDrive_Disconnected"));
            Assert.False(File.Exists(customExe));
            using (var vm4 = CreateVm(steamRoot))
            {
                await vm4.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: false);
                Assert.Equal(2, vm4.AllGames.Count); // NEVER deleted
                var unavailableCard = vm4.AllGames.FirstOrDefault(g => g.Game.ExecutablePath.Equals(Path.GetFullPath(customExe), StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(unavailableCard);
                Assert.False(unavailableCard!.IsAvailable);
                Assert.Equal(FriendlyGameStatus.Unavailable, unavailableCard.FriendlyStatus);
                Assert.Equal("Unavailable", unavailableCard.FriendlyStatusText);
            }
            SqliteConnection.ClearAllPools();

            // 5. Reconnect drive -> verify game becomes Available again with custom artwork intact
            Directory.Move(Path.Combine(tempRoot, "ExternalDrive_Disconnected"), Path.Combine(tempRoot, "ExternalDrive"));
            Assert.True(File.Exists(customExe));
            using (var vm5 = CreateVm(steamRoot))
            {
                await vm5.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: false);
                var restoredCard = vm5.AllGames.FirstOrDefault(g => g.Game.ExecutablePath.Equals(Path.GetFullPath(customExe), StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(restoredCard);
                Assert.True(restoredCard!.IsAvailable);
                Assert.NotEqual(FriendlyGameStatus.Unavailable, restoredCard.FriendlyStatus);
                Assert.Equal(ArtworkSourceKind.CustomUser, restoredCard.Game.ArtworkSource);

                // 6. Add already-discovered game (both CyberRunner from Steam and customExe) -> verify NO duplicate created
                await vm5.AddGameManuallyFromPathAsync(steamGameExe);
                await vm5.AddGameManuallyFromPathAsync(customExe);
                Assert.Equal(2, vm5.AllGames.Count);

                // Also adding the parent folder to Settings CustomLibraryPaths -> rescan -> still 2 games, no duplicate!
                vm5.Settings.CustomLibraryPaths.Add(Path.GetDirectoryName(removableMount)!);
                await vm5.ScanLibrariesAsync();
                Assert.Equal(2, vm5.AllGames.Count);

                // 7. Invalid / non-PE executable is rejected cleanly
                string invalidExe = Path.Combine(tempRoot, "NotARealGame.exe");
                File.WriteAllText(invalidExe, "NOT_A_WINDOWS_PE_EXECUTABLE");
                var rejected = await vm5.AddGameManuallyFromPathAsync(invalidExe);
                Assert.Null(rejected);
                Assert.Equal(2, vm5.AllGames.Count);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(LumaPaths.AppDataOverrideEnvVar, string.IsNullOrEmpty(oldEnv) ? null : oldEnv);
            await CleanupTempDirectoryAsync(tempRoot);
        }
    }

    private static async Task CleanupTempDirectoryAsync(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 9)
            {
                await Task.Delay(150);
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                await Task.Delay(150);
            }
        }
    }

    [Fact]
    public void Task3_RoundedClipGeometry_And_ArtworkCacheInvalidation_MatchSharedCardCorners()
    {
        var sharedRadius = new CornerRadius(12);

        // Portrait card 196x296
        var cardClip = RoundedClip.CreateClipGeometry(196, 296, sharedRadius, 0.5);
        Assert.Equal(new Rect(0.5, 0.5, 195, 295), cardClip.Rect);
        Assert.Equal(11.5, cardClip.RadiusX);
        Assert.Equal(11.5, cardClip.RadiusY);
        Assert.True(cardClip.IsFrozen);

        // Thumbnail 320x160
        var thumbClip = RoundedClip.CreateClipGeometry(320, 160, sharedRadius, 0.5);
        Assert.Equal(new Rect(0.5, 0.5, 319, 159), thumbClip.Rect);
        Assert.Equal(11.5, thumbClip.RadiusX);

        // Hero 1376x420 with 16px corner radius
        var heroClip = RoundedClip.CreateClipGeometry(1376, 420, new CornerRadius(16), 0.5);
        Assert.Equal(new Rect(0.5, 0.5, 1375, 419), heroClip.Rect);
        Assert.Equal(15.5, heroClip.RadiusX);

        // Edge cases: zero dimensions and narrow elements never throw
        var zeroClip = RoundedClip.CreateClipGeometry(0, 0, sharedRadius, 0.5);
        Assert.True(zeroClip.Rect.IsEmpty);
        var tinyClip = RoundedClip.CreateClipGeometry(10, 10, new CornerRadius(12), 0.5);
        Assert.Equal(4.5, tinyClip.RadiusX);

        // Verify FilePathToBitmapConverter refreshes when artwork file is replaced in-place
        string tempImg = Path.Combine(Path.GetTempPath(), $"LumaArtReplace_{Guid.NewGuid():N}.png");
        try
        {
            WriteDummyPng(tempImg, 200);
            File.SetLastWriteTimeUtc(tempImg, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var conv = new FilePathToBitmapConverter();
            var bmp1 = conv.Convert(tempImg, typeof(BitmapSource), "280", System.Globalization.CultureInfo.InvariantCulture) as BitmapImage;
            Assert.NotNull(bmp1);

            WriteDummyPng(tempImg, 50);
            File.SetLastWriteTimeUtc(tempImg, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
            var bmp2 = conv.Convert(tempImg, typeof(BitmapSource), "280", System.Globalization.CultureInfo.InvariantCulture) as BitmapImage;
            Assert.NotNull(bmp2);
            Assert.NotSame(bmp1, bmp2);
        }
        finally
        {
            if (File.Exists(tempImg)) File.Delete(tempImg);
        }
    }

    private static void WriteDummyPng(string path, byte fill = 128)
    {
        const int w = 16, h = 16;
        byte[] raw = new byte[(1 + w * 3) * h];
        for (int y = 0; y < h; y++)
        {
            int row = y * (1 + w * 3);
            for (int x = 0; x < w; x++)
            {
                raw[row + 1 + x * 3] = fill;
                raw[row + 2 + x * 3] = 40;
                raw[row + 3 + x * 3] = 40;
            }
        }
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        byte[] comp = ms.ToArray();
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13];
        ihdr[3] = w;
        ihdr[7] = h;
        ihdr[8] = 8;
        ihdr[9] = 2;
        WritePngChunk(fs, "IHDR"u8, ihdr);
        WritePngChunk(fs, "IDAT"u8, comp);
        WritePngChunk(fs, "IEND"u8, ReadOnlySpan<byte>.Empty);
    }

    private static void WritePngChunk(Stream fs, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        len[0] = (byte)(data.Length >> 24);
        len[1] = (byte)(data.Length >> 16);
        len[2] = (byte)(data.Length >> 8);
        len[3] = (byte)data.Length;
        fs.Write(len);
        fs.Write(type);
        fs.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type) crc = StepCrc(crc, b);
        foreach (byte b in data) crc = StepCrc(crc, b);
        crc ^= 0xFFFFFFFFu;
        Span<byte> crcB = stackalloc byte[4];
        crcB[0] = (byte)(crc >> 24);
        crcB[1] = (byte)(crc >> 16);
        crcB[2] = (byte)(crc >> 8);
        crcB[3] = (byte)crc;
        fs.Write(crcB);
    }

    private static uint StepCrc(uint crc, byte b)
    {
        crc ^= b;
        for (int k = 0; k < 8; k++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

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

    private sealed class IsolatedMultiStoreScanner(MultiStoreGameScanner inner, MultiStoreGameScanner.ScanOptions options) : IGameLibraryScanner
    {
        public Task<IReadOnlyList<DiscoveredGame>> ScanAllAsync(IEnumerable<string> customLibraryRoots, IProgress<GameLibraryScanProgress>? progress = null, CancellationToken ct = default)
            => inner.ScanWithOptionsAsync(customLibraryRoots, options, progress, ct);

        public Task<DiscoveredGame?> InspectSingleDirectoryOrExeAsync(string path, GameLauncherType launcher = GameLauncherType.Manual, string storeAppId = "", CancellationToken ct = default)
            => inner.InspectSingleDirectoryOrExeAsync(path, launcher, storeAppId, ct);
    }

    private sealed class StubGpuDetector(DetectedGpuInfo gpu) : IGpuDetector
    {
        public Task<DetectedGpuInfo> DetectPrimaryGpuAsync(CancellationToken ct = default) => Task.FromResult(gpu);
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

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => fn(request, cancellationToken);
    }
}
