using System.Text.Json;
using Microsoft.Win32;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.GameDiscovery;

/// <summary>Cancellable background game-library scanner covering Steam, Epic, GOG, Ubisoft, EA, Battle.net, Xbox, and custom folders.</summary>
public sealed class MultiStoreGameScanner : IGameLibraryScanner
{
    private static readonly string[] SteamToolAppIds = { "228980", "1070560", "1391110", "1628350" };
    private readonly ILumaLogger _logger;
    private readonly GameDirectoryInspector _inspector;

    public MultiStoreGameScanner(ILumaLogger logger, IPeAnalyzer peAnalyzer)
    {
        _logger = logger;
        _inspector = new GameDirectoryInspector(peAnalyzer);
    }

    /// <summary>Optional overrides for isolated unit/integration testing without touching host registry.</summary>
    public sealed class ScanOptions
    {
        public string? SteamRootOverride { get; set; }
        public string? EpicManifestsOverride { get; set; }
        public IReadOnlyList<string>? XboxDriveRootsOverride { get; set; }
        public bool DisableRegistry { get; set; }
    }

    public Task<IReadOnlyList<DiscoveredGame>> ScanAllAsync(
        IEnumerable<string> customLibraryRoots,
        IProgress<GameLibraryScanProgress>? progress = null,
        CancellationToken ct = default)
        => ScanWithOptionsAsync(customLibraryRoots, new ScanOptions(), progress, ct);

    public async Task<IReadOnlyList<DiscoveredGame>> ScanWithOptionsAsync(
        IEnumerable<string> customLibraryRoots,
        ScanOptions options,
        IProgress<GameLibraryScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var rawEntries = new List<(GameLauncherType Launcher, string AppId, string Title, string Path)>();

            var sources = new (string Label, Action Collect)[]
            {
                ("Steam", () => CollectSteam(options, rawEntries)),
                ("Epic Games", () => CollectEpic(options, rawEntries)),
                ("GOG Galaxy", () => { if (!options.DisableRegistry) CollectGog(rawEntries); }),
                ("Ubisoft Connect", () => { if (!options.DisableRegistry) CollectUbisoft(rawEntries); }),
                ("EA App", () => { if (!options.DisableRegistry) CollectUninstallByPublisher(rawEntries, GameLauncherType.EaApp, "Electronic Arts", new[] { "EA app", "EA Desktop", "Origin" }); }),
                ("Battle.net", () => { if (!options.DisableRegistry) CollectUninstallByPublisher(rawEntries, GameLauncherType.BattleNet, "Blizzard Entertainment", new[] { "Battle.net" }); }),
                ("Xbox / Microsoft Store", () => CollectXbox(options, rawEntries)),
                ("Custom Libraries", () => CollectCustomRoots(customLibraryRoots, rawEntries))
            };

            for (int i = 0; i < sources.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new GameLibraryScanProgress
                {
                    CurrentSource = sources[i].Label,
                    DiscoveredCount = rawEntries.Count,
                    PercentComplete = (int)((i / (double)sources.Length) * 50),
                    StatusMessage = $"Scanning {sources[i].Label}..."
                });

                try
                {
                    sources[i].Collect();
                }
                catch (Exception ex)
                {
                    _logger.Warn("GameDiscovery", $"Store scanner {sources[i].Label} encountered an error", ex.Message);
                }
            }

            // Deduplicate by normalized path while preserving store priority
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uniqueCandidates = new List<(GameLauncherType Launcher, string AppId, string Title, string Path)>();
            foreach (var entry in rawEntries)
            {
                try
                {
                    string normalized = Path.GetFullPath(entry.Path).TrimEnd('\\', '/');
                    if (!Directory.Exists(normalized) && !File.Exists(normalized)) continue;
                    if (seenDirs.Add(normalized))
                        uniqueCandidates.Add((entry.Launcher, entry.AppId, entry.Title, normalized));
                }
                catch { }
            }

            var results = new List<DiscoveredGame>();
            for (int i = 0; i < uniqueCandidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var c = uniqueCandidates[i];
                progress?.Report(new GameLibraryScanProgress
                {
                    CurrentSource = c.Launcher.ToString(),
                    DiscoveredCount = results.Count,
                    PercentComplete = 50 + (int)(((i + 1) / (double)Math.Max(1, uniqueCandidates.Count)) * 50),
                    StatusMessage = $"Inspecting {c.Title}..."
                });

                var inspected = _inspector.Inspect(c.Path, c.Title, c.Launcher, c.AppId);
                if (inspected != null) results.Add(inspected);
            }

            _logger.Info("GameDiscovery", $"Library scan complete: {results.Count} games discovered across {sources.Length} sources.");
            return (IReadOnlyList<DiscoveredGame>)results.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }, ct);
    }

    public Task<DiscoveredGame?> InspectSingleDirectoryOrExeAsync(
        string path,
        GameLauncherType launcher = GameLauncherType.Manual,
        string storeAppId = "",
        CancellationToken ct = default)
    {
        return Task.Run(() => _inspector.Inspect(path, string.Empty, launcher, storeAppId), ct);
    }

    private void CollectSteam(ScanOptions options, List<(GameLauncherType, string, string, string)> output)
    {
        string? steamRoot = options.SteamRootOverride;
        if (string.IsNullOrWhiteSpace(steamRoot) && !options.DisableRegistry)
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            steamRoot = key?.GetValue("SteamPath") as string;
        }
        if (string.IsNullOrWhiteSpace(steamRoot)) return;
        steamRoot = Path.GetFullPath(steamRoot.Replace('/', '\\'));

        var libraryFolders = new List<string> { steamRoot };
        string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdfPath))
        {
            var parsed = VdfParser.Parse(File.ReadAllText(vdfPath));
            var libs = VdfParser.GetSubBlock(parsed, "libraryfolders");
            if (libs != null)
            {
                foreach (var kv in libs)
                {
                    string? path = kv.Value as string;
                    if (kv.Value is Dictionary<string, object> sub)
                        path = VdfParser.GetString(sub, "path");

                    if (!string.IsNullOrWhiteSpace(path))
                        libraryFolders.Add(path.Replace('/', '\\'));
                }
            }
        }

        var visitedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string lib in libraryFolders)
        {
            string steamApps = Path.Combine(Path.GetFullPath(lib), "steamapps");
            if (!visitedApps.Add(steamApps) || !Directory.Exists(steamApps)) continue;

            foreach (string acf in Directory.GetFiles(steamApps, "appmanifest_*.acf"))
            {
                var appState = VdfParser.GetSubBlock(VdfParser.Parse(File.ReadAllText(acf)), "AppState");
                string? appId = VdfParser.GetString(appState, "appid");
                string? title = VdfParser.GetString(appState, "name");
                string? installDir = VdfParser.GetString(appState, "installdir");

                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(installDir)) continue;
                // Guard against path traversal in tampered ACF files
                if (installDir.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || installDir.Contains("..")) continue;
                if (SteamToolAppIds.Contains(appId)) continue;
                if (title != null && (title.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
                                      title.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)))
                    continue;

                output.Add((GameLauncherType.Steam, appId, title ?? installDir, Path.Combine(steamApps, "common", installDir)));
            }
        }
    }

    private void CollectEpic(ScanOptions options, List<(GameLauncherType, string, string, string)> output)
    {
        string manifestsDir = options.EpicManifestsOverride ??
                              (options.DisableRegistry
                                  ? string.Empty
                                  : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                      "Epic", "EpicGamesLauncher", "Data", "Manifests"));
        if (string.IsNullOrWhiteSpace(manifestsDir) || !Directory.Exists(manifestsDir)) return;

        foreach (string itemFile in Directory.GetFiles(manifestsDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(itemFile));
                var root = doc.RootElement;
                string? location = root.TryGetProperty("InstallLocation", out var locProp) ? locProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(location)) continue;

                string appId = (root.TryGetProperty("CatalogItemId", out var catProp) ? catProp.GetString() : null)
                               ?? (root.TryGetProperty("AppName", out var appProp) ? appProp.GetString() : null)
                               ?? Path.GetFileNameWithoutExtension(itemFile);
                string title = (root.TryGetProperty("DisplayName", out var dispProp) ? dispProp.GetString() : null)
                               ?? Path.GetFileName(location);

                output.Add((GameLauncherType.EpicGames, appId, title, location));
            }
            catch { }
        }
    }

    private static void CollectGog(List<(GameLauncherType, string, string, string)> output)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var gogKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
            if (gogKey == null) continue;

            foreach (string id in gogKey.GetSubKeyNames())
            {
                using var g = gogKey.OpenSubKey(id);
                string? path = g?.GetValue("path") as string;
                string? name = g?.GetValue("gameName") as string;
                if (!string.IsNullOrWhiteSpace(path))
                    output.Add((GameLauncherType.GogGalaxy, id, name ?? Path.GetFileName(path), path));
            }
        }
    }

    private static void CollectUbisoft(List<(GameLauncherType, string, string, string)> output)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var ubiKey = baseKey.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
            if (ubiKey == null) continue;

            foreach (string id in ubiKey.GetSubKeyNames())
            {
                using var g = ubiKey.OpenSubKey(id);
                string? dir = g?.GetValue("InstallDir") as string;
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    string clean = dir.Replace('/', '\\').TrimEnd('\\');
                    output.Add((GameLauncherType.UbisoftConnect, id, Path.GetFileName(clean), clean));
                }
            }
        }
    }

    private static void CollectUninstallByPublisher(
        List<(GameLauncherType, string, string, string)> output,
        GameLauncherType launcher,
        string publisherMatch,
        string[] launcherPrefixes)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninst = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninst == null) continue;

            foreach (string id in uninst.GetSubKeyNames())
            {
                using var item = uninst.OpenSubKey(id);
                string? pub = item?.GetValue("Publisher") as string;
                string? loc = item?.GetValue("InstallLocation") as string;
                string? name = item?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(pub) || !pub.Contains(publisherMatch, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(loc)) continue;
                if (name != null && launcherPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                output.Add((launcher, id, name ?? Path.GetFileName(loc.Trim('"')), loc.Trim('"')));
            }
        }
    }

    private static void CollectXbox(ScanOptions options, List<(GameLauncherType, string, string, string)> output)
    {
        IEnumerable<string> driveRoots = options.XboxDriveRootsOverride ??
                                         DriveInfo.GetDrives()
                                             .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                                             .Select(d => d.RootDirectory.FullName);

        foreach (string driveRoot in driveRoots)
        {
            if (!File.Exists(Path.Combine(driveRoot, ".GamingRoot"))) continue;
            string xboxGames = Path.Combine(driveRoot, "XboxGames");
            if (!Directory.Exists(xboxGames)) continue;

            foreach (string gameDir in Directory.GetDirectories(xboxGames))
            {
                string title = Path.GetFileName(gameDir);
                string contentDir = Path.Combine(gameDir, "Content");
                output.Add((GameLauncherType.Xbox, title, title, Directory.Exists(contentDir) ? contentDir : gameDir));
            }
        }
    }

    private static void CollectCustomRoots(IEnumerable<string> customRoots, List<(GameLauncherType, string, string, string)> output)
    {
        foreach (string root in customRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            // If root directly holds an exe, treat root as a single game; otherwise inspect immediate subdirectories
            if (Directory.GetFiles(root, "*.exe").Length > 0)
            {
                output.Add((GameLauncherType.Manual, string.Empty, Path.GetFileName(root.TrimEnd('\\', '/')), root));
            }
            else
            {
                foreach (string sub in Directory.GetDirectories(root))
                    output.Add((GameLauncherType.Manual, string.Empty, Path.GetFileName(sub), sub));
            }
        }
    }
}
