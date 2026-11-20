using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
using ProjectLuma.UI;
using ProjectLuma.UI.ViewModels;
using ProjectLuma.UI.Views;
using ProjectLuma.UpdateService;

namespace ProjectLuma.App;

public partial class App : Application
{
    private IHost? _host;
    private WindowsSystemTrayService? _trayService;
    private bool _isShuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--health-check", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(0);
            return;
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton<ILumaLogger>(sp => new LumaLogger());
                services.AddSingleton<IGpuDetector, WindowsGpuDetector>();
                services.AddSingleton<ICompatibilityRepository, SqliteCompatibilityRepository>();
                services.AddSingleton<ICapabilityEngine, CapabilityEngine>();
                services.AddSingleton<IPeAnalyzer, PeAnalyzer>();
                services.AddSingleton<IArtworkDiscoveryService, ArtworkDiscoveryService>();
                services.AddSingleton<IGameLibraryScanner, MultiStoreGameScanner>();
                services.AddSingleton<IUserDllVault, UserDllVault>();
                services.AddSingleton<IPackageManager>(sp => new GitHubPackageManager(
                    sp.GetRequiredService<ICompatibilityRepository>(),
                    sp.GetRequiredService<ILumaLogger>()));
                services.AddSingleton<IBackendOrchestrator, BackendOrchestrator>();
                services.AddSingleton<IGameInstaller, TransactionalGameInstaller>();
                services.AddSingleton<IGameLaunchService, GameLaunchService>();
                services.AddSingleton<IIntelligentComponentResolver, IntelligentComponentResolver>();
                services.AddSingleton<UpdateCoordinatorService>();
                services.AddSingleton<IUpdateCoordinator>(sp => sp.GetRequiredService<UpdateCoordinatorService>());
                services.AddHostedService(sp => sp.GetRequiredService<UpdateCoordinatorService>());
                services.AddSingleton<IUserDialogService, WindowsUserDialogService>();
                services.AddSingleton<IWindowsStartupService>(_ => new WindowsStartupService());
                services.AddSingleton<TrayLifecycleController>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        string? screenshotDir = GetArg(e.Args, "--capture-screenshots");
        string? diagnosticsOut = GetArg(e.Args, "--dump-diagnostics");

        var vm = _host.Services.GetRequiredService<MainViewModel>();
        await vm.InitializeAsync(autoScan: true, showOnboardingIfFirstRun: string.IsNullOrWhiteSpace(screenshotDir));

        if (!string.IsNullOrWhiteSpace(diagnosticsOut))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(diagnosticsOut))!);
            var payload = new
            {
                generatedUtc = DateTime.UtcNow,
                hardwareProfile = vm.HardwareProfile,
                detectedGpu = vm.GpuInfo,
                neuralHub = vm.LastNeuralHubReport,
                discoveredGames = vm.AllGames.Select(g => new
                {
                    g.Title,
                    g.LauncherBadge,
                    g.ApiBadge,
                    g.EngineName,
                    g.ExecutableName,
                    g.FriendlyStatusText,
                    ArtworkSource = g.Game.ArtworkSource.ToString(),
                    HasCoverImage = !string.IsNullOrWhiteSpace(g.CoverImagePath) && File.Exists(g.CoverImagePath),
                    HasHeroImage = !string.IsNullOrWhiteSpace(g.HeroImagePath) && File.Exists(g.HeroImagePath),
                    g.HasLogoImage,
                    g.NeuralState,
                    g.SuperResState,
                    g.FrameGenState,
                    g.RayReconState,
                    g.Game.AntiCheatStatus
                })
            };
            await File.WriteAllTextAsync(diagnosticsOut, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }

        if (!string.IsNullOrWhiteSpace(screenshotDir))
        {
            await RunVisualVerificationCaptureAsync(vm, screenshotDir);
            await _host.StopAsync();
            Shutdown(0);
            return;
        }

        await _host.StartAsync();

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var trayController = _host.Services.GetRequiredService<TrayLifecycleController>();
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;

        _trayService = new WindowsSystemTrayService(
            trayController,
            () => vm.Settings.MinimizeToTray,
            () => ExecuteExplicitAppExit(mainWindow));
        _trayService.Attach(mainWindow);
        vm.TrayPreferenceSyncRequested += () => _trayService?.RefreshPreferenceState();
        vm.ApplicationUpdateRestartRequested += () => Dispatcher.Invoke(() => ExecuteExplicitAppExit(mainWindow));

        mainWindow.Closed += (_, _) =>
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;
            _trayService?.Dispose();
            _trayService = null;
            Shutdown(0);
        };

        bool startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (startMinimized && vm.Settings.MinimizeToTray)
        {
            mainWindow.ShowInTaskbar = false;
            mainWindow.WindowState = WindowState.Minimized;
            mainWindow.Show();
            trayController.HandleWindowClosing(true);
        }
        else
        {
            mainWindow.Show();
        }
    }

    private void ExecuteExplicitAppExit(Window mainWindow)
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;
        _trayService?.Dispose();
        _trayService = null;
        if (mainWindow.IsLoaded)
            mainWindow.Close();
        Shutdown(0);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        _trayService = null;
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        base.OnExit(e);
    }

    private async Task RunVisualVerificationCaptureAsync(MainViewModel vm, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        // Seed realistic visual-QA fixture cards ONLY when explicitly invoked with --capture-screenshots
        if (vm.AllGames.Count < 6)
        {
            var sampleGames = vm.AllGames.Select(c => c.Game).ToList();
            sampleGames.Add(new DiscoveredGame
            {
                Id = "verify-cp2077",
                Title = "Cyberpunk 2077",
                ExecutablePath = @"C:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
                ExecutableDirectory = @"C:\Games\Cyberpunk 2077\bin\x64",
                InstallDirectory = @"C:\Games\Cyberpunk 2077",
                Launcher = GameLauncherType.Steam,
                StoreAppId = "1091500",
                GraphicsApi = GraphicsApi.DirectX12,
                EngineName = "REDengine 4",
                Is64Bit = true,
                ImportsVersionDll = true,
                NativeDlssSupported = true,
                NativeFsrSupported = true,
                NativeXeSsSupported = true,
                InstalledBackendId = "optiscaler",
                InstalledBackendVersion = "v0.9.5"
            });
            sampleGames.Add(new DiscoveredGame
            {
                Id = "verify-rdr2",
                Title = "Red Dead Redemption 2",
                ExecutablePath = @"C:\Games\RDR2\RDR2.exe",
                ExecutableDirectory = @"C:\Games\RDR2",
                InstallDirectory = @"C:\Games\RDR2",
                Launcher = GameLauncherType.Steam,
                StoreAppId = "1174180",
                GraphicsApi = GraphicsApi.Vulkan,
                EngineName = "RAGE Engine",
                Is64Bit = true,
                NativeDlssSupported = true,
                NativeFsrSupported = true
            });
            sampleGames.Add(new DiscoveredGame
            {
                Id = "verify-alanwake2",
                Title = "Alan Wake 2",
                ExecutablePath = @"C:\Games\Alan Wake 2\AlanWake2.exe",
                ExecutableDirectory = @"C:\Games\Alan Wake 2",
                InstallDirectory = @"C:\Games\Alan Wake 2",
                Launcher = GameLauncherType.EpicGames,
                GraphicsApi = GraphicsApi.DirectX12,
                EngineName = "Northlight Engine",
                Is64Bit = true,
                NativeDlssSupported = true,
                NativeFsrSupported = true,
                NativeFrameGenSupported = true
            });
            sampleGames.Add(new DiscoveredGame
            {
                Id = "verify-fh5",
                Title = "Forza Horizon 5",
                ExecutablePath = @"C:\Games\Forza Horizon 5\ForzaHorizon5.exe",
                ExecutableDirectory = @"C:\Games\Forza Horizon 5",
                InstallDirectory = @"C:\Games\Forza Horizon 5",
                Launcher = GameLauncherType.Xbox,
                StoreAppId = "1551360",
                GraphicsApi = GraphicsApi.DirectX12,
                EngineName = "ForzaTech",
                Is64Bit = true,
                NativeFsrSupported = true,
                NativeXeSsSupported = true
            });
            sampleGames.Add(new DiscoveredGame
            {
                Id = "verify-gtav-longtitle",
                Title = "Grand Theft Auto V Enhanced: Extremely Long Subtitle Regression Check Edition (Offline Only)",
                ExecutablePath = @"C:\Games\GTAV\GTA5_Enhanced.exe",
                ExecutableDirectory = @"C:\Games\GTAV",
                InstallDirectory = @"C:\Games\GTAV",
                Launcher = GameLauncherType.EpicGames,
                GraphicsApi = GraphicsApi.DirectX12,
                EngineName = "RAGE Engine",
                Is64Bit = true,
                AntiCheatStatus = AntiCheatStatus.DetectedOfflineAllowed,
                AntiCheatDetails = "BattlEye detected; offline Story Mode (-nobattleye) only"
            });
            await vm.PopulateGamesAsync(sampleGames, resolveArtwork: true);
        }

        // Populate Downloads & Updates view items for visual verification
        var coordinator = _host!.Services.GetRequiredService<IUpdateCoordinator>();
        try { await coordinator.CheckNowAsync(); } catch { }

        var window = _host.Services.GetRequiredService<MainWindow>();
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.Left = -10000;
        window.Top = -10000;
        window.Show();

        var profiles = new (string Tag, double Width, double Height, double Scale)[]
        {
            ("1080p_100pct", 1920, 1080, 1.00),
            ("1080p_125pct", 1920, 1080, 1.25),
            ("1080p_150pct", 1920, 1080, 1.50),
            ("1080p_175pct", 1920, 1080, 1.75),
            ("1080p_200pct", 1920, 1080, 2.00),
            ("720p_100pct",  1280, 720,  1.00),
            ("768p_100pct",  1366, 768,  1.00),
            ("1440p_100pct", 2560, 1440, 1.00),
            ("2160p_100pct", 3840, 2160, 1.00)
        };

        var pages = new[]
        {
            LumaPage.Home,
            LumaPage.MyGames,
            LumaPage.GameDetails,
            LumaPage.EnhancementLibrary,
            LumaPage.Downloads,
            LumaPage.Settings
        };

        async Task CaptureFrameAsync(string fileName, double wPx, double hPx, double scale)
        {
            double logicalW = wPx / scale;
            double logicalH = hPx / scale;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            if (window.Content is FrameworkElement root)
            {
                root.LayoutTransform = Transform.Identity;
                root.Measure(new Size(logicalW, logicalH));
                root.Arrange(new Rect(0, 0, logicalW, logicalH));
                root.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

                double dpi = 96.0 * scale;
                var rtb = new RenderTargetBitmap((int)wPx, (int)hPx, dpi, dpi, PixelFormats.Pbgra32);
                rtb.Render(root);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                string file = Path.Combine(outputDir, fileName);
                await using var fs = File.Create(file);
                encoder.Save(fs);
            }
        }

        foreach (var (tag, wPx, hPx, scale) in profiles)
        {
            double logicalW = wPx / scale;
            double logicalH = hPx / scale;
            window.Width = logicalW;
            window.Height = logicalH;
            vm.UpdateGridViewportWidth(Math.Max(400, logicalW - 228 - 64));

            vm.IsToastVisible = false;
            foreach (var page in pages)
            {
                if (tag != "1080p_100pct" && tag != "720p_100pct" && page is LumaPage.EnhancementLibrary or LumaPage.Downloads)
                    continue;

                vm.ActivePage = page;
                await CaptureFrameAsync($"{page}_{tag}.png", wPx, hPx, scale);
            }
        }

        // Special interactive state captures at 1080p 100%
        window.Width = 1920;
        window.Height = 1080;
        vm.ActivePage = LumaPage.GameDetails;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Expand Advanced Settings on GameDetails and capture
        var detailsExpander = FindVisibleVisualChild<System.Windows.Controls.Expander>(window);
        if (detailsExpander != null)
        {
            detailsExpander.IsExpanded = true;
            await CaptureFrameAsync("GameDetails_AdvancedExpanded_1080p_100pct.png", 1920, 1080, 1.0);
            detailsExpander.IsExpanded = false;
        }

        // Expand EnhancementLibrary component technical details and capture
        vm.ActivePage = LumaPage.EnhancementLibrary;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var optiItem = vm.ComponentItems.FirstOrDefault(c => c.Id == "optiscaler");
        if (optiItem != null)
        {
            optiItem.IsDetailsExpanded = true;
            await CaptureFrameAsync("EnhancementLibrary_Expanded_1080p_100pct.png", 1920, 1080, 1.0);
            optiItem.IsDetailsExpanded = false;
        }

        // Dedicated NVIDIA Neural Rendering DLL Import panel capture (v3.3.0)
        string prevCategory = vm.SelectedEnhancementCategory;
        vm.SelectedEnhancementCategory = "AI Visual Enhancement";
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var nrItem = vm.ComponentItems.FirstOrDefault(c => c.Id == "dlss-nr-on-amd");
        if (nrItem != null)
        {
            nrItem.IsDetailsExpanded = true;
            await CaptureFrameAsync("EnhancementLibrary_NeuralDllImport_1080p_100pct.png", 1920, 1080, 1.0);
            vm.IsNeuralDiagnosticsExpanded = true;
            await CaptureFrameAsync("EnhancementLibrary_NeuralHubDiagnostics_1080p_100pct.png", 1920, 1080, 1.0);
            vm.IsNeuralDiagnosticsExpanded = false;
            nrItem.IsDetailsExpanded = false;
        }
        vm.SelectedEnhancementCategory = prevCategory;

        // Native Frame Generation "Already available in this game" capture (v3.3.0)
        var prevGame = vm.SelectedGame;
        var nativeFgGame = vm.AllGames.FirstOrDefault(g => g.Game.NativeFrameGenSupported) ?? prevGame;
        vm.SelectedGame = nativeFgGame;
        vm.ActivePage = LumaPage.GameDetails;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await CaptureFrameAsync("GameDetails_GameplaySmoothness_1080p_100pct.png", 1920, 1080, 1.0);
        vm.SelectedGame = prevGame;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // AI Visuals 7-Stage Discovery Setup Drawer capture (live NeuralHub state)
        vm.IsAiVisualsSetupDrawer = true;
        vm.SetupDrawerHeader = "SETTING UP AI VISUAL ENHANCEMENT";
        vm.SetupSteps.Clear();
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Checking GPU compatibility", Detail = "AMD Radeon RX 6600 (gfx1032) checked", State = SetupStepState.Complete });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Checking game compatibility", Detail = $"{vm.SelectedGame?.Title ?? "Game"} (DirectX 12, 64-bit) compatible", State = SetupStepState.Complete });
        vm.SetupSteps.Add(vm.IsNeuralHubDetected
            ? new SetupProgressStep { Label = "Checking Neural Rendering backend", Detail = vm.LastNeuralHubReport.StatusHeadline, State = SetupStepState.Complete }
            : new SetupProgressStep { Label = "Checking Neural Rendering backend", Detail = "Install NeuralHub v0.9.0 from official GitHub release", State = SetupStepState.AttentionRequired });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Scanning game folders", Detail = "Checked registered game directories (depth <= 3)", State = SetupStepState.Complete });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Scanning Downloads", Detail = "Downloads watched automatically for local components", State = SetupStepState.Complete });
        vm.SetupSteps.Add(vm.LastNeuralHubReport.IsDllImportedInNeuralHub || vm.HasImportedNeuralDll
            ? new SetupProgressStep { Label = "Validating model DLL", Detail = vm.NeuralLocalInstallationStatusText, State = SetupStepState.Complete }
            : new SetupProgressStep { Label = "Validating model DLL", Detail = "Requires user-owned nvngx_dlssnr.dll (>= 310.9.16.0 for CC)", State = SetupStepState.AttentionRequired });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Checking runtime initialization", Detail = "amdhip64_6.dll initialized; amdhip64_7.dll absent on RX 6600", State = SetupStepState.AttentionRequired });
        vm.IsSetupDrawerOpen = true;
        await CaptureFrameAsync("GameDetails_AiVisualsSetupDrawer_1080p_100pct.png", 1920, 1080, 1.0);
        vm.IsSetupDrawerOpen = false;
        vm.IsAiVisualsSetupDrawer = false;
        vm.SetupDrawerHeader = "Setting up game enhancements";

        // Settings Windows Startup & Minimize to Tray capture (v3.3.0)
        vm.ActivePage = LumaPage.Settings;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await CaptureFrameAsync("Settings_StartupAndTray_1080p_100pct.png", 1920, 1080, 1.0);

        vm.ActivePage = LumaPage.GameDetails;
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Guided 6-step Setup Drawer capture
        vm.SetupSteps.Clear();
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Checking graphics compatibility", Detail = "AMD Radeon RX 6600 (gfx1032) verified", State = SetupStepState.Complete });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Selecting recommended enhancements", Detail = "Balanced profile selected", State = SetupStepState.Complete });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Preparing required components", Detail = "OptiScaler package ready", State = SetupStepState.Complete });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Downloading enhancement files", Detail = "Verifying SHA-256 checksum", State = SetupStepState.InProgress });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Preparing game configuration", Detail = "Pending", State = SetupStepState.Pending });
        vm.SetupSteps.Add(new SetupProgressStep { Label = "Creating a recovery point", Detail = "Pending", State = SetupStepState.Pending });
        vm.IsSetupDrawerOpen = true;
        await CaptureFrameAsync("SetupDrawer_1080p_100pct.png", 1920, 1080, 1.0);
        vm.IsSetupDrawerOpen = false;

        // First-Run 3-Step Onboarding captures (Step 1 & Step 3)
        vm.ActivePage = LumaPage.Home;
        vm.OnboardingStepIndex = 1;
        vm.IsOnboardingVisible = true;
        await CaptureFrameAsync("Onboarding_Step1_1080p_100pct.png", 1920, 1080, 1.0);
        vm.OnboardingStepIndex = 3;
        await CaptureFrameAsync("Onboarding_Step3_1080p_100pct.png", 1920, 1080, 1.0);
        vm.IsOnboardingVisible = false;

        // In-Game Notification Overlay capture
        var overlay = new LumaLaunchOverlayWindow
        {
            ShowInTaskbar = false,
            Left = -10000,
            Top = -10000,
            Opacity = 1
        };
        overlay.Show();
        overlay.ConfigureContent("Aniimo", "BALANCED", "Sharper Image (FSR 4.1.2 INT8) · Smoother Gameplay (OptiFG DX12)");
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (overlay.Content is FrameworkElement ovRoot)
        {
            ovRoot.Measure(new Size(368, 220));
            ovRoot.Arrange(new Rect(0, 0, ovRoot.DesiredSize.Width, ovRoot.DesiredSize.Height));
            ovRoot.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            int ow = Math.Max(368, (int)Math.Ceiling(ovRoot.ActualWidth));
            int oh = Math.Max(140, (int)Math.Ceiling(ovRoot.ActualHeight));
            var rtb = new RenderTargetBitmap(ow, oh, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(ovRoot);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            await using var ovFs = File.Create(Path.Combine(outputDir, "InGameNotification_1080p_100pct.png"));
            enc.Save(ovFs);
        }
        overlay.Close();

        window.Close();
    }

    private static T? FindVisibleVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement ui && ui.Visibility != Visibility.Visible) continue;
            if (child is T match) return match;
            var nested = FindVisibleVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
