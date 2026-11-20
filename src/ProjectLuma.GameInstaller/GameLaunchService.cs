using System.Diagnostics;
using Microsoft.Win32;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.GameInstaller;

/// <summary>Store-aware game pre-flight validator and launcher with truthful active enhancement reporting.</summary>
public sealed class GameLaunchService : IGameLaunchService
{
    private readonly IGameInstaller _installer;
    private readonly ILumaLogger _logger;

    /// <summary>Optional test dispatcher hook to verify generated launch info without spawning external store processes in unit tests.</summary>
    public Func<ProcessStartInfo, bool>? ProcessStarterOverride { get; set; }

    /// <summary>Milliseconds to wait for a newly launched game process to appear before reporting LauncherInvoked vs Running (set to 0 in fast unit tests).</summary>
    public int LaunchConfirmationTimeoutMs { get; set; } = 3000;

    public GameLaunchService(IGameInstaller installer, ILumaLogger logger)
    {
        _installer = installer;
        _logger = logger;
    }

    public async Task<GameLaunchResult> LaunchGameAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        IProgress<GameLaunchPhase>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report(GameLaunchPhase.Starting);

        if (game == null || string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
        {
            progress?.Report(GameLaunchPhase.LaunchFailed);
            return new GameLaunchResult
            {
                Succeeded = false,
                Phase = GameLaunchPhase.LaunchFailed,
                ActiveProfileName = profile?.SelectedPreset.ToString() ?? "Standard",
                ActiveEnhancementsSummary = "Unavailable",
                Message = "Game executable could not be found on disk. Rescan libraries or relocate the game."
            };
        }

        if (game.AntiCheatStatus == AntiCheatStatus.DetectedBlocked)
        {
            // Online anti-cheat games launch cleanly in unenhanced mode; Project Luma never injects into blocked games
            _logger.Info("GameLaunch", $"Launching {game.Title} without injectors due to online anti-cheat protection.");
        }

        // Check if already running
        var running = _installer.DetectRunningProcessesInDirectory(game.ExecutableDirectory);
        if (running.Count > 0)
        {
            progress?.Report(GameLaunchPhase.Running);
            return new GameLaunchResult
            {
                Succeeded = true,
                Phase = GameLaunchPhase.Running,
                ActiveProfileName = !string.IsNullOrWhiteSpace(game.InstalledBackendId)
                    ? profile.SelectedPreset.ToString().ToUpperInvariant()
                    : "NATIVE",
                ActiveEnhancementsSummary = await BuildTruthfulEnhancementsSummaryAsync(game, profile, ct),
                Message = $"{game.Title} is already running."
            };
        }

        progress?.Report(GameLaunchPhase.PreparingEnhancements);
        string summary = await BuildTruthfulEnhancementsSummaryAsync(game, profile, ct);
        string activeProfileLabel = !string.IsNullOrWhiteSpace(game.InstalledBackendId) &&
                                    game.HealthState == IntegrationHealthState.Healthy
            ? $"{profile.SelectedPreset.ToString().ToUpperInvariant()}"
            : "STANDARD";

        progress?.Report(GameLaunchPhase.Launching);
        var psi = BuildLaunchProcessStartInfo(game);
        bool usedStoreProtocol = IsStoreProtocolLaunch(psi, game);

        try
        {
            bool started = ProcessStarterOverride != null
                ? ProcessStarterOverride(psi)
                : Process.Start(psi) != null;

            if (!started)
            {
                progress?.Report(GameLaunchPhase.LaunchFailed);
                return new GameLaunchResult
                {
                    Succeeded = false,
                    Phase = GameLaunchPhase.LaunchFailed,
                    ActiveProfileName = activeProfileLabel,
                    ActiveEnhancementsSummary = summary,
                    Message = $"Store launcher ({game.Launcher}) declined to start {game.Title}. Verify the client is signed in or launch directly from the store."
                };
            }

            if (ProcessStarterOverride == null && usedStoreProtocol && LaunchConfirmationTimeoutMs > 0)
            {
                progress?.Report(GameLaunchPhase.LauncherInvoked);
                var deadline = DateTime.UtcNow.AddMilliseconds(LaunchConfirmationTimeoutMs);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    await Task.Delay(350, ct);
                    if (_installer.DetectRunningProcessesInDirectory(game.ExecutableDirectory).Count > 0)
                    {
                        _logger.Info("GameLaunch", $"Confirmed running game process for {game.Title} via '{psi.FileName}'.", summary);
                        progress?.Report(GameLaunchPhase.Running);
                        return new GameLaunchResult
                        {
                            Succeeded = true,
                            Phase = GameLaunchPhase.Running,
                            ActiveProfileName = activeProfileLabel,
                            ActiveEnhancementsSummary = summary,
                            Message = $"Launched {game.Title}."
                        };
                    }
                }

                // Store launcher opened cleanly, but game process has not yet spawned (DRM/cloud check/updater)
                _logger.Info("GameLaunch", $"Opened {game.Launcher} launcher for {game.Title} via '{psi.FileName}'.", summary);
                progress?.Report(GameLaunchPhase.LauncherInvoked);
                return new GameLaunchResult
                {
                    Succeeded = true,
                    Phase = GameLaunchPhase.LauncherInvoked,
                    ActiveProfileName = activeProfileLabel,
                    ActiveEnhancementsSummary = summary,
                    Message = $"{game.Launcher} launcher opened for {game.Title}."
                };
            }

            var finalPhase = usedStoreProtocol ? GameLaunchPhase.LauncherInvoked : GameLaunchPhase.Running;
            _logger.Info("GameLaunch", $"Launched {game.Title} ({game.Launcher}) via '{psi.FileName}' [{finalPhase}].", summary);
            progress?.Report(finalPhase);
            return new GameLaunchResult
            {
                Succeeded = true,
                Phase = finalPhase,
                ActiveProfileName = activeProfileLabel,
                ActiveEnhancementsSummary = summary,
                Message = usedStoreProtocol ? $"{game.Launcher} launcher opened for {game.Title}." : $"Launched {game.Title}."
            };
        }
        catch (Exception ex)
        {
            _logger.Error("GameLaunch", $"Failed to launch {game.Title}", ex);
            progress?.Report(GameLaunchPhase.LaunchFailed);
            return new GameLaunchResult
            {
                Succeeded = false,
                Phase = GameLaunchPhase.LaunchFailed,
                ActiveProfileName = activeProfileLabel,
                ActiveEnhancementsSummary = summary,
                Message = $"Could not launch {game.Title}: {ex.Message}"
            };
        }
    }

    public static ProcessStartInfo BuildLaunchProcessStartInfo(DiscoveredGame game)
    {
        switch (game.Launcher)
        {
            case GameLauncherType.Steam when !string.IsNullOrWhiteSpace(game.StoreAppId) && uint.TryParse(game.StoreAppId, out _):
                return new ProcessStartInfo($"steam://rungameid/{game.StoreAppId}") { UseShellExecute = true };

            case GameLauncherType.EpicGames when !string.IsNullOrWhiteSpace(game.StoreAppId):
                return new ProcessStartInfo($"com.epicgames.launcher://apps/{Uri.EscapeDataString(game.StoreAppId)}?action=launch&silent=true") { UseShellExecute = true };

            case GameLauncherType.GogGalaxy when !string.IsNullOrWhiteSpace(game.StoreAppId):
            {
                string? galaxyExe = FindGogGalaxyClientExe();
                if (!string.IsNullOrWhiteSpace(galaxyExe))
                    return new ProcessStartInfo(galaxyExe, $"/command=runGame /gameId={game.StoreAppId}") { UseShellExecute = true };
                break;
            }

            case GameLauncherType.UbisoftConnect when !string.IsNullOrWhiteSpace(game.StoreAppId):
                return new ProcessStartInfo($"uplay://launch/{Uri.EscapeDataString(game.StoreAppId)}/0") { UseShellExecute = true };

            case GameLauncherType.EaApp when !string.IsNullOrWhiteSpace(game.StoreAppId):
                return new ProcessStartInfo($"origin2://game/launch?offerIds={Uri.EscapeDataString(game.StoreAppId)}") { UseShellExecute = true };

            case GameLauncherType.BattleNet when !string.IsNullOrWhiteSpace(game.StoreAppId):
                return new ProcessStartInfo($"battlenet://{Uri.EscapeDataString(game.StoreAppId)}") { UseShellExecute = true };

            case GameLauncherType.Xbox when !string.IsNullOrWhiteSpace(game.StoreAppId):
                return new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{game.StoreAppId}") { UseShellExecute = true };
        }

        return new ProcessStartInfo(game.ExecutablePath)
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(game.ExecutableDirectory)
                ? Path.GetDirectoryName(game.ExecutablePath) ?? string.Empty
                : game.ExecutableDirectory,
            UseShellExecute = true
        };
    }

    private static bool IsStoreProtocolLaunch(ProcessStartInfo psi, DiscoveredGame game)
    {
        if (psi.FileName.Contains("://", StringComparison.Ordinal))
            return true;
        if (psi.FileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
            psi.Arguments.Contains("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            return true;
        if (game.Launcher == GameLauncherType.GogGalaxy &&
            psi.FileName.EndsWith("GalaxyClient.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string? FindGogGalaxyClientExe()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths")
                         ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\GalaxyClient\paths");
            string? dir = key?.GetValue("client") as string;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string exe = Path.Combine(dir, "GalaxyClient.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return null;
    }

    private async Task<string> BuildTruthfulEnhancementsSummaryAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game.InstalledBackendId))
            return "Native Game Rendering";

        var health = await _installer.VerifyInstallationHealthAsync(game, ct);
        game.HealthState = health;

        if (health != IntegrationHealthState.Healthy)
            return $"Unverified integration ({health})";

        var active = new List<string>();
        if (game.InstalledFeatures.Contains(RenderingFeatureType.SuperResolution) ||
            (profile.EnableSuperResolution && game.InstalledBackendId.Contains("optiscaler", StringComparison.OrdinalIgnoreCase)))
        {
            active.Add("Image Quality");
        }

        if (game.InstalledFeatures.Contains(RenderingFeatureType.FrameGeneration) ||
            (profile.EnableFrameGeneration && !game.NativeFrameGenSupported &&
             profile.FrameGenerationState == CapabilityState.Supported))
        {
            active.Add("Gameplay Smoothness");
        }

        if (game.InstalledFeatures.Contains(RenderingFeatureType.NeuralRendering) ||
            (profile.EnableNeuralRendering &&
             profile.NeuralRenderingState is CapabilityState.Supported or CapabilityState.Experimental))
        {
            active.Add("AI Visuals");
        }

        if (game.InstalledFeatures.Contains(RenderingFeatureType.RayReconstruction))
            active.Add("Ray-Traced Effects");

        return active.Count > 0
            ? string.Join(Environment.NewLine, active)
            : "Verified enhancements active";
    }
}
