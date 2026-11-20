using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Models;

namespace ProjectLuma.Core.Services;

/// <summary>
/// Read-only inspector for upstream NeuralHub (danielblnc/DLSS-NR-on-AMD) installations,
/// runtime payload verification, data\index.ini DLL store, Project Luma Vault SHA-256 sync,
/// and per-game games.json / proxy stub / OptiScaler conflict analysis.
/// Never modifies, duplicates, or deletes any user or game files.
/// </summary>
public static class NeuralHubInspector
{
    private const uint HKEY_CURRENT_USER = 0x80000001u;
    private const uint HKEY_LOCAL_MACHINE = 0x80000002u;
    private const int KEY_READ = 0x20019;
    private const int KEY_WOW64_64KEY = 0x0100;
    private const int KEY_WOW64_32KEY = 0x0200;
    private const uint REG_SZ = 1;
    private const uint REG_EXPAND_SZ = 2;
    private const uint REG_DWORD = 4;

    private sealed record StoreCandidate(
        string Directory,
        string Source,
        string? RegistryRuntimePath = null,
        string? RegistryRuntimeVersion = null,
        string? RegistryRuntimeAbi = null);

    public static NeuralHubDetectionResultInfo Detect(
        string? stagedInstallPath = null,
        NeuralHubProbeOptions? options = null)
    {
        var report = InspectFullStatus(stagedInstallPath, null, null, options);
        bool isLegacyStagedOnly = !report.IsAppDetected && HasRuntimeBinaryPayload(stagedInstallPath);
        if (isLegacyStagedOnly)
        {
            return new NeuralHubDetectionResultInfo
            {
                IsDetected = true,
                InstallDirectory = stagedInstallPath!,
                ExecutablePath = FindNeuralHubExecutable(stagedInstallPath!) ?? string.Empty,
                Version = report.RuntimeVersion.Length > 0 ? report.RuntimeVersion : "v0.9.0",
                Summary = $"NeuralHub runtime staged cleanly at {stagedInstallPath}."
            };
        }

        string ver = !string.IsNullOrWhiteSpace(report.RuntimeVersion)
            ? report.RuntimeVersion
            : (!string.IsNullOrWhiteSpace(report.AppVersion) ? EnsureLeadingV(report.AppVersion) : "v0.9.0");

        return new NeuralHubDetectionResultInfo
        {
            IsDetected = report.IsAppDetected && report.IsRuntimeVerified,
            InstallDirectory = report.StorePath,
            ExecutablePath = report.ExecutablePath,
            Version = ver,
            Summary = report.StatusHeadline
        };
    }

    public static NeuralHubStatusReport InspectFullStatus(
        string? stagedInstallPath = null,
        UserImportedDllRecord? vaultedNrDll = null,
        IEnumerable<DiscoveredGame>? discoveredGames = null,
        NeuralHubProbeOptions? options = null,
        bool includeGames = true)
    {
        var report = new NeuralHubStatusReport();
        var candidates = DiscoverStoreCandidates(stagedInstallPath, options);

        StoreCandidate? chosenStore = null;
        string foundExe = string.Empty;
        string foundRuntimeDir = string.Empty;
        string foundRuntimeVersion = string.Empty;

        foreach (var cand in candidates)
        {
            if (string.IsNullOrWhiteSpace(cand.Directory) || !Directory.Exists(cand.Directory))
                continue;

            string? exe = FindNeuralHubExecutable(cand.Directory);
            var (rtDir, rtVer, hasMarker, hasHost) = ResolveRuntimeDirectory(cand.Directory, cand.RegistryRuntimePath, cand.RegistryRuntimeVersion);

            bool strongMatch = exe != null || hasMarker || hasHost;
            if (!strongMatch)
                continue;

            chosenStore = cand;
            foundExe = exe ?? string.Empty;
            foundRuntimeDir = rtDir;
            foundRuntimeVersion = rtVer;
            break;
        }

        if (chosenStore != null)
        {
            report.StorePath = chosenStore.Directory;
            report.DetectionSource = chosenStore.Source;
            report.ExecutablePath = foundExe;
            report.IsAppDetected = !string.IsNullOrWhiteSpace(foundExe) && File.Exists(foundExe);

            if (report.IsAppDetected)
                report.AppVersion = ReadFileVersionSafe(foundExe);

            if (!string.IsNullOrWhiteSpace(chosenStore.RegistryRuntimeAbi))
                report.RuntimeAbi = chosenStore.RegistryRuntimeAbi;

            if (!string.IsNullOrWhiteSpace(foundRuntimeDir) && Directory.Exists(foundRuntimeDir))
            {
                report.RuntimePath = foundRuntimeDir;
                report.RuntimeVersion = EnsureLeadingV(!string.IsNullOrWhiteSpace(foundRuntimeVersion)
                    ? foundRuntimeVersion
                    : Path.GetFileName(foundRuntimeDir));
                report.HasPayloadMarker = File.Exists(Path.Combine(foundRuntimeDir, ".nh_payload"));
                report.HasHostDll = File.Exists(Path.Combine(foundRuntimeDir, "neuralhub_host.dll"));
                report.HasBundledFsrUpscaler = File.Exists(Path.Combine(foundRuntimeDir, "ffx", "amd_fidelityfx_upscaler_dx12.dll"));
                report.HasBundledFsrFrameGen = File.Exists(Path.Combine(foundRuntimeDir, "ffx", "amd_fidelityfx_framegeneration_dx12.dll"));
                report.IsRuntimeVerified =
                    (report.HasPayloadMarker && report.HasHostDll) ||
                    File.Exists(Path.Combine(foundRuntimeDir, "NeuralHub.dll"));
            }
            else if (report.IsAppDetected && !string.IsNullOrWhiteSpace(report.AppVersion))
            {
                report.RuntimeVersion = EnsureLeadingV(report.AppVersion);
            }

            if (report.IsRuntimeVerified && !report.IsAppDetected && Directory.Exists(chosenStore.Directory))
            {
                // Stage-only custom archive test fallback
                report.IsAppDetected = File.Exists(Path.Combine(foundRuntimeDir, "NeuralHub.dll"));
            }

            InspectNeuralHubIndexIni(chosenStore.Directory, report);
            if (includeGames)
                InspectRegisteredGames(chosenStore.Directory, discoveredGames, report);
        }

        InspectVaultSynchronization(vaultedNrDll, report);
        PopulateHeadlinesAndDiagnostics(report);
        return report;
    }

    private static readonly object HostQuickCacheLock = new();
    private static NeuralHubStatusReport? _cachedQuickHostReport;
    private static long _cachedQuickHostTicks;

    public static void InvalidateCache()
    {
        lock (HostQuickCacheLock)
        {
            _cachedQuickHostReport = null;
            _cachedQuickHostTicks = 0;
        }
    }

    public static NeuralHubStatusReport InspectHostQuickCached(NeuralHubProbeOptions? options = null)
    {
        if (options != null)
            return InspectFullStatus(null, null, null, options, includeGames: false);

        long now = Environment.TickCount64;
        lock (HostQuickCacheLock)
        {
            if (_cachedQuickHostReport != null && (now - _cachedQuickHostTicks) < 2000)
                return _cachedQuickHostReport;
        }

        var fresh = InspectFullStatus(null, null, null, null, includeGames: false);
        lock (HostQuickCacheLock)
        {
            _cachedQuickHostReport = fresh;
            _cachedQuickHostTicks = Environment.TickCount64;
        }
        return fresh;
    }

    public static NeuralHubGameIntegrationStatus InspectGameIntegration(
        DiscoveredGame game,
        NeuralHubStatusReport? report)
    {
        if (report != null && report.RegisteredGames.Count > 0)
        {
            var matched = report.RegisteredGames.FirstOrDefault(rg => GameMatchesIntegration(game, rg));
            if (matched != null)
                return matched;
        }

        return InspectGameDirectoryCore(
            gameId: game.Id,
            gameName: game.Title,
            gameDir: game.ExecutableDirectory,
            executableName: Path.GetFileName(game.ExecutablePath),
            registryKey: game.Launcher == GameLauncherType.Steam && !string.IsNullOrWhiteSpace(game.StoreAppId)
                ? $"steam:{game.StoreAppId}"
                : string.Empty,
            expectedProxyDll: string.Empty,
            isRegisteredInNeuralHub: false);
    }

    public static bool HasRuntimeBinaryPayload(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return false;

        try
        {
            if (File.Exists(Path.Combine(directoryPath, ".nh_payload")) &&
                File.Exists(Path.Combine(directoryPath, "neuralhub_host.dll")))
            {
                return true;
            }

            if (File.Exists(Path.Combine(directoryPath, "neuralhub.exe")) &&
                ResolveRuntimeDirectory(directoryPath, null, null).HasHostDll)
            {
                return true;
            }

            bool hasExe = Directory.EnumerateFiles(directoryPath, "*.exe", SearchOption.AllDirectories)
                .Any(p => !Path.GetFileName(p).Contains("setup", StringComparison.OrdinalIgnoreCase));
            bool hasDll = Directory.EnumerateFiles(directoryPath, "*.dll", SearchOption.AllDirectories)
                .Any(p => !Path.GetFileName(p).StartsWith("nvngx_", StringComparison.OrdinalIgnoreCase));
            return hasExe || hasDll;
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<StoreCandidate> DiscoverStoreCandidates(string? stagedInstallPath, NeuralHubProbeOptions? options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool TryAccept(string? dir, string source, string? rtPath, string? rtVer, string? rtAbi, out StoreCandidate? item)
        {
            item = null;
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                string full = Path.GetFullPath(dir.Trim());
                if (seen.Add(full))
                {
                    item = new StoreCandidate(full, source, rtPath, rtVer, rtAbi);
                    return true;
                }
            }
            catch { }
            return false;
        }

        if (options != null)
        {
            if (TryAccept(options.ExplicitRegistryStorePath,
                    @"Registry (HKCU\Software\Danielblnc\NeuralHub)",
                    options.ExplicitRegistryRuntimePath,
                    options.ExplicitRegistryRuntimeVersion,
                    options.ExplicitRegistryRuntimeAbi,
                    out var explicitReg) && explicitReg != null)
                yield return explicitReg;

            foreach (string extra in options.CandidateDirectories)
                if (TryAccept(extra, "Configured Probe Candidate", null, null, null, out var ex) && ex != null)
                    yield return ex;
        }

        if (options?.DisableHostProbe != true)
        {
            if (OperatingSystem.IsWindows() && (options == null || options.IncludeRegistry))
            {
                var (store, rtPath, rtVer, rtAbi) = ReadDanielblncRegistryKey();
                if (TryAccept(store, @"Registry (HKCU\Software\Danielblnc\NeuralHub)", rtPath, rtVer, rtAbi, out var regCand) && regCand != null)
                    yield return regCand;
            }

            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (TryAccept(Path.Combine(localApp, "Danielblnc", "NeuralHub"), @"LocalAppData\Danielblnc\NeuralHub", null, null, null, out var defCand) && defCand != null)
                yield return defCand;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (TryAccept(Path.Combine(localApp, "Programs", "NeuralHub"), @"LocalAppData\Programs\NeuralHub", null, null, null, out var p1) && p1 != null)
                yield return p1;
            if (TryAccept(Path.Combine(localApp, "NeuralHub"), @"LocalAppData\NeuralHub", null, null, null, out var p2) && p2 != null)
                yield return p2;
            if (TryAccept(Path.Combine(appData, "NeuralHub"), @"AppData\NeuralHub", null, null, null, out var p3) && p3 != null)
                yield return p3;
            if (TryAccept(Path.Combine(progFiles, "NeuralHub"), @"ProgramFiles\NeuralHub", null, null, null, out var p4) && p4 != null)
                yield return p4;

            if (OperatingSystem.IsWindows() && (options == null || options.IncludeRegistry))
            {
                foreach (string uninstallDir in FindUninstallRegistryInstallDirs())
                    if (TryAccept(uninstallDir, "Windows Uninstall Registry", null, null, null, out var uCand) && uCand != null)
                        yield return uCand;
            }

            if (options == null || options.IncludeRunningProcesses)
            {
                foreach (string procDir in FindRunningNeuralHubProcessDirs())
                    if (TryAccept(procDir, "Running neuralhub.exe Process", null, null, null, out var prCand) && prCand != null)
                        yield return prCand;
            }
        }

        if (TryAccept(stagedInstallPath, "Project Luma Staged Path", null, null, null, out var stCand) && stCand != null)
            yield return stCand;
    }

    private static string? FindNeuralHubExecutable(string storeDir)
    {
        try
        {
            string canonical = Path.Combine(storeDir, "neuralhub.exe");
            if (File.Exists(canonical))
                return canonical;

            return Directory.EnumerateFiles(storeDir, "*neuralhub*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(p => !Path.GetFileName(p).Contains("setup", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static (string RuntimeDir, string RuntimeVersion, bool HasPayloadMarker, bool HasHostDll) ResolveRuntimeDirectory(
        string storeDir,
        string? registryRuntimePath,
        string? registryRuntimeVersion)
    {
        var probeDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(registryRuntimePath))
            probeDirs.Add(registryRuntimePath.Trim());
        if (!string.IsNullOrWhiteSpace(registryRuntimeVersion))
        {
            string cleanVer = EnsureLeadingV(registryRuntimeVersion.Trim());
            probeDirs.Add(Path.Combine(storeDir, "runtime", cleanVer));
            probeDirs.Add(Path.Combine(storeDir, "runtime", cleanVer.TrimStart('v', 'V')));
        }

        string runtimeRoot = Path.Combine(storeDir, "runtime");
        if (Directory.Exists(runtimeRoot))
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(runtimeRoot).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                    probeDirs.Add(sub);
            }
            catch { }
        }
        probeDirs.Add(storeDir);

        foreach (string dir in probeDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            bool marker = File.Exists(Path.Combine(dir, ".nh_payload"));
            bool host = File.Exists(Path.Combine(dir, "neuralhub_host.dll"));
            bool legacyDll = File.Exists(Path.Combine(dir, "NeuralHub.dll"));
            if (marker || host || legacyDll)
            {
                string folderName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                string ver = !string.IsNullOrWhiteSpace(registryRuntimeVersion)
                    ? EnsureLeadingV(registryRuntimeVersion)
                    : (folderName.StartsWith("v", StringComparison.OrdinalIgnoreCase) && folderName.Contains('.')
                        ? folderName
                        : "v0.9.0");
                return (dir, ver, marker, host);
            }
        }

        return (string.Empty, EnsureLeadingV(registryRuntimeVersion ?? string.Empty), false, false);
    }

    private static void InspectNeuralHubIndexIni(string storeDir, NeuralHubStatusReport report)
    {
        string indexPath = Path.Combine(storeDir, "data", "index.ini");
        if (!File.Exists(indexPath))
            return;

        try
        {
            bool inNrSection = false;
            string file = string.Empty;
            string sha = string.Empty;
            string fileVer = string.Empty;
            string models = string.Empty;

            foreach (string raw in File.ReadAllLines(indexPath))
            {
                string line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
                    continue;

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string section = line[1..^1].Trim();
                    inNrSection = section.Equals("nr", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inNrSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim();
                if (key.Equals("File", StringComparison.OrdinalIgnoreCase)) file = val;
                else if (key.Equals("Sha256", StringComparison.OrdinalIgnoreCase)) sha = val.ToUpperInvariant();
                else if (key.Equals("FileVersion", StringComparison.OrdinalIgnoreCase)) fileVer = val;
                else if (key.Equals("Models", StringComparison.OrdinalIgnoreCase)) models = val;
            }

            if (string.IsNullOrWhiteSpace(file))
                return;

            string fullDllPath = Path.IsPathRooted(file)
                ? file
                : Path.Combine(storeDir, "data", file);

            if (!File.Exists(fullDllPath))
                return;

            if (string.IsNullOrWhiteSpace(sha))
                sha = ComputeSha256Upper(fullDllPath);
            if (string.IsNullOrWhiteSpace(fileVer))
                fileVer = ReadFileVersionSafe(fullDllPath);

            report.IsDllImportedInNeuralHub = true;
            report.NeuralHubDllFileName = Path.GetFileName(fullDllPath);
            report.NeuralHubDllFullPath = fullDllPath;
            report.NeuralHubDllSha256 = sha;
            report.NeuralHubDllVersion = fileVer;
            report.NeuralHubSupportedModels = models;
        }
        catch { }
    }

    private static void InspectVaultSynchronization(UserImportedDllRecord? vaultedNrDll, NeuralHubStatusReport report)
    {
        bool vaultValid = vaultedNrDll != null &&
                          !string.IsNullOrWhiteSpace(vaultedNrDll.StoredPath) &&
                          File.Exists(vaultedNrDll.StoredPath);

        if (vaultValid && vaultedNrDll != null)
        {
            report.IsDllImportedInLumaVault = true;
            report.LumaVaultDllPath = vaultedNrDll.StoredPath;
            report.LumaVaultDllVersion = vaultedNrDll.FileVersion;
            report.LumaVaultDllSha256 = string.IsNullOrWhiteSpace(vaultedNrDll.Sha256)
                ? ComputeSha256Upper(vaultedNrDll.StoredPath)
                : vaultedNrDll.Sha256.ToUpperInvariant();
        }

        if (report.IsDllImportedInNeuralHub && report.IsDllImportedInLumaVault)
        {
            report.IsVaultSynchronizedWithNeuralHub = string.Equals(
                report.NeuralHubDllSha256,
                report.LumaVaultDllSha256,
                StringComparison.OrdinalIgnoreCase);

            report.DllSyncSummary = report.IsVaultSynchronizedWithNeuralHub
                ? $"nvngx_dlssnr.dll v{report.NeuralHubDllVersion} imported in both NeuralHub and Project Luma Vault (SHA-256 verified match; no duplication)."
                : $"NeuralHub has v{report.NeuralHubDllVersion} while Project Luma Vault has v{report.LumaVaultDllVersion} (SHA-256 differs; zero forced overwrite).";
        }
        else if (report.IsDllImportedInNeuralHub)
        {
            report.DllSyncSummary = $"nvngx_dlssnr.dll v{report.NeuralHubDllVersion} imported in NeuralHub ({report.NeuralHubSupportedModels}); Project Luma recognizes it directly without duplicating files.";
        }
        else if (report.IsDllImportedInLumaVault)
        {
            report.DllSyncSummary = report.IsAppDetected
                ? $"nvngx_dlssnr.dll v{report.LumaVaultDllVersion} vaulted in Project Luma; import into NeuralHub store via Open NeuralHub."
                : $"nvngx_dlssnr.dll v{report.LumaVaultDllVersion} vaulted in Project Luma; install NeuralHub v0.9.0 to activate.";
        }
        else
        {
            report.DllSyncSummary = "No imported nvngx_dlssnr.dll found in NeuralHub store or Project Luma Vault.";
        }
    }

    private static void InspectRegisteredGames(
        string storeDir,
        IEnumerable<DiscoveredGame>? discoveredGames,
        NeuralHubStatusReport report)
    {
        string gamesJsonPath = Path.Combine(storeDir, "games.json");
        if (File.Exists(gamesJsonPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(gamesJsonPath));
                if (doc.RootElement.TryGetProperty("games", out var gamesArr) && gamesArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var g in gamesArr.EnumerateArray())
                    {
                        string id = GetJsonString(g, "id");
                        string name = GetJsonString(g, "name");
                        string dir = GetJsonString(g, "dir");
                        string overDir = GetJsonString(g, "over_dir");
                        string exe = GetJsonString(g, "exe");
                        string proxy = GetJsonString(g, "proxy");
                        string key = GetJsonString(g, "key");

                        string targetDir = Directory.Exists(dir)
                            ? dir
                            : (Directory.Exists(overDir) ? overDir : dir);

                        var status = InspectGameDirectoryCore(id, name, targetDir, exe, key, proxy, isRegisteredInNeuralHub: true);
                        report.RegisteredGames.Add(status);
                    }
                }
            }
            catch { }
        }

        if (discoveredGames == null)
            return;

        foreach (var dg in discoveredGames)
        {
            bool alreadyListed = report.RegisteredGames.Any(rg => GameMatchesIntegration(dg, rg));
            if (alreadyListed)
                continue;

            var extra = InspectGameDirectoryCore(
                dg.Id,
                dg.Title,
                dg.ExecutableDirectory,
                Path.GetFileName(dg.ExecutablePath),
                dg.Launcher == GameLauncherType.Steam && !string.IsNullOrWhiteSpace(dg.StoreAppId) ? $"steam:{dg.StoreAppId}" : string.Empty,
                expectedProxyDll: string.Empty,
                isRegisteredInNeuralHub: false);

            if (extra.IsGameLoaderInstalled || extra.IsGameConfigured || extra.HasRuntimeLogInitialized)
                report.RegisteredGames.Add(extra);
        }
    }

    private static NeuralHubGameIntegrationStatus InspectGameDirectoryCore(
        string gameId,
        string gameName,
        string gameDir,
        string executableName,
        string registryKey,
        string expectedProxyDll,
        bool isRegisteredInNeuralHub)
    {
        var status = new NeuralHubGameIntegrationStatus
        {
            GameId = gameId,
            GameName = string.IsNullOrWhiteSpace(gameName) ? gameId : gameName,
            GameDirectory = gameDir,
            ExecutableName = executableName,
            RegistryKey = registryKey,
            ExpectedProxyDll = string.IsNullOrWhiteSpace(expectedProxyDll) ? "winmm.dll" : expectedProxyDll,
            IsRegisteredInNeuralHub = isRegisteredInNeuralHub
        };

        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            status.StatusSummary = isRegisteredInNeuralHub
                ? "Registered in NeuralHub, but target directory was not found on disk."
                : "Game directory unavailable.";
            return status;
        }

        string neuralIni = Path.Combine(gameDir, "neuralhub.ini");
        status.ConfigIniPath = neuralIni;
        status.IsGameConfigured = File.Exists(neuralIni);

        string nrLog = Path.Combine(gameDir, "dlssnr_on_amd.log");
        string stubLog = Path.Combine(gameDir, "neuralhub_stub.log");
        if (File.Exists(nrLog))
        {
            status.HasRuntimeLogInitialized = true;
            status.RuntimeLogPath = nrLog;
        }
        else if (File.Exists(stubLog))
        {
            status.HasRuntimeLogInitialized = true;
            status.RuntimeLogPath = stubLog;
        }

        string proxyPath = Path.Combine(gameDir, status.ExpectedProxyDll);
        status.ProxyDllPath = proxyPath;
        bool proxyExists = File.Exists(proxyPath);
        if (proxyExists)
        {
            try { status.ProxyDllSizeBytes = new FileInfo(proxyPath).Length; } catch { }
        }

        bool hasOptiIni = File.Exists(Path.Combine(gameDir, "OptiScaler.ini"));
        bool hasOptiDir = Directory.Exists(Path.Combine(gameDir, "D3D12_Optiscaler"));
        bool looksLikeOptiProxy = (hasOptiIni || hasOptiDir) &&
                                  proxyExists &&
                                  (!status.IsGameConfigured || status.ProxyDllSizeBytes > 2 * 1024 * 1024);

        if (looksLikeOptiProxy)
        {
            status.HasOptiScalerProxyConflict = true;
            status.IsGameLoaderInstalled = false;
            double mb = status.ProxyDllSizeBytes / (1024d * 1024d);
            status.ConflictDetail = $"OptiScaler owns '{status.ExpectedProxyDll}' ({mb:0.0} MB) and OptiScaler.ini in '{gameDir}'; Project Luma will not silently overwrite OptiScaler.";
        }
        else if (proxyExists && (status.IsGameConfigured || status.HasRuntimeLogInitialized || status.ProxyDllSizeBytes <= 2 * 1024 * 1024))
        {
            status.IsGameLoaderInstalled = true;
        }

        if (status.IsGameLoaderInstalled && status.IsGameConfigured && status.HasRuntimeLogInitialized)
            status.StatusSummary = $"NeuralHub loader ({status.ExpectedProxyDll}), neuralhub.ini, and runtime log verified.";
        else if (status.HasOptiScalerProxyConflict)
            status.StatusSummary = $"Registered in NeuralHub; OptiScaler active on {status.ExpectedProxyDll} (conflict preserved safely).";
        else if (status.IsGameLoaderInstalled || status.IsGameConfigured)
            status.StatusSummary = "NeuralHub configured in game folder (launch game once to generate dlssnr_on_amd.log).";
        else if (isRegisteredInNeuralHub)
            status.StatusSummary = $"Registered in NeuralHub ({status.ExpectedProxyDll}); game loader not deployed yet.";
        else
            status.StatusSummary = "Not yet added to NeuralHub.";

        return status;
    }

    private static bool GameMatchesIntegration(DiscoveredGame game, NeuralHubGameIntegrationStatus rg)
    {
        if (!string.IsNullOrWhiteSpace(rg.RegistryKey) &&
            game.Launcher == GameLauncherType.Steam &&
            !string.IsNullOrWhiteSpace(game.StoreAppId) &&
            rg.RegistryKey.Equals($"steam:{game.StoreAppId}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(rg.GameDirectory))
        {
            try
            {
                string rgDir = Path.GetFullPath(rg.GameDirectory).TrimEnd('\\', '/');
                if (!string.IsNullOrWhiteSpace(game.ExecutableDirectory) &&
                    Path.GetFullPath(game.ExecutableDirectory).TrimEnd('\\', '/').Equals(rgDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (!string.IsNullOrWhiteSpace(game.InstallDirectory) &&
                    Path.GetFullPath(game.InstallDirectory).TrimEnd('\\', '/').Equals(rgDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch { }
        }

        return !string.IsNullOrWhiteSpace(rg.ExecutableName) &&
               !string.IsNullOrWhiteSpace(game.ExecutablePath) &&
               Path.GetFileName(game.ExecutablePath).Equals(rg.ExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    private static void PopulateHeadlinesAndDiagnostics(NeuralHubStatusReport report)
    {
        string ver = !string.IsNullOrWhiteSpace(report.RuntimeVersion)
            ? report.RuntimeVersion
            : (!string.IsNullOrWhiteSpace(report.AppVersion) ? EnsureLeadingV(report.AppVersion) : "v0.9.0");

        if (report.IsAppDetected && report.IsRuntimeVerified)
            report.StatusHeadline = $"NeuralHub {ver} — Installed and detected";
        else if (report.IsAppDetected)
            report.StatusHeadline = $"NeuralHub app detected at {report.StorePath}; runtime folder needs verification";
        else
            report.StatusHeadline = "NeuralHub v0.9.0 not installed (acquire from official GitHub releases)";

        var sb = new StringBuilder();
        sb.AppendLine($"1. NeuralHub App: {(report.IsAppDetected ? $"Detected ({report.ExecutablePath}, v{report.AppVersion}) via {report.DetectionSource}" : "Not detected")}");
        sb.AppendLine($"2. Runtime Payload ({ver}): {(report.IsRuntimeVerified ? $"Verified at {report.RuntimePath} (.nh_payload={report.HasPayloadMarker}, host={report.HasHostDll}, ffx_upscaler={report.HasBundledFsrUpscaler}, ffx_fg={report.HasBundledFsrFrameGen})" : "Not verified")}");
        sb.AppendLine($"3. NeuralHub DLL Store: {(report.IsDllImportedInNeuralHub ? $"{report.NeuralHubDllFileName} v{report.NeuralHubDllVersion} [{report.NeuralHubSupportedModels}] SHA-256={report.NeuralHubDllSha256}" : "Not imported in data\\index.ini")}");
        sb.AppendLine($"4. Project Luma Vault Sync: {report.DllSyncSummary}");
        if (report.RegisteredGames.Count == 0)
        {
            sb.AppendLine("5-7. Games: No entries in NeuralHub games.json yet.");
        }
        else
        {
            foreach (var g in report.RegisteredGames)
            {
                sb.AppendLine($"5-7. Game '{g.GameName}' ({g.ExpectedProxyDll}): LoaderInstalled={g.IsGameLoaderInstalled}, Configured={g.IsGameConfigured}, RuntimeLog={g.HasRuntimeLogInitialized}, OptiScalerConflict={g.HasOptiScalerProxyConflict} — {g.StatusSummary}");
            }
        }
        report.DiagnosticsSummary = sb.ToString().TrimEnd();
    }

    private static (string StorePath, string RuntimePath, string RuntimeVersion, string RuntimeAbi) ReadDanielblncRegistryKey()
    {
        if (!OperatingSystem.IsWindows())
            return (string.Empty, string.Empty, string.Empty, string.Empty);

        foreach (int view in new[] { KEY_WOW64_64KEY, KEY_WOW64_32KEY, 0 })
        {
            if (RegOpenKeyExW(HKEY_CURRENT_USER, @"Software\Danielblnc\NeuralHub", 0, KEY_READ | view, out IntPtr hKey) == 0)
            {
                try
                {
                    return (
                        QueryRegValueString(hKey, "StorePath"),
                        QueryRegValueString(hKey, "RuntimePath"),
                        QueryRegValueString(hKey, "RuntimeVersion"),
                        QueryRegValueString(hKey, "RuntimeAbi"));
                }
                finally
                {
                    RegCloseKey(hKey);
                }
            }
        }

        return (string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private static IEnumerable<string> FindUninstallRegistryInstallDirs()
    {
        var results = new List<string>();
        if (!OperatingSystem.IsWindows())
            return results;

        const string subKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var root in new[] { HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE })
        {
            foreach (int view in new[] { KEY_WOW64_64KEY, KEY_WOW64_32KEY })
            {
                if (RegOpenKeyExW(root, subKey, 0, KEY_READ | view, out IntPtr hUninstall) != 0)
                    continue;

                try
                {
                    uint index = 0;
                    var nameBuf = new char[256];
                    while (true)
                    {
                        uint nameLen = (uint)nameBuf.Length;
                        int rc = RegEnumKeyExW(hUninstall, index++, nameBuf, ref nameLen, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        if (rc != 0) break;

                        string childName = new(nameBuf, 0, (int)nameLen);
                        if (RegOpenKeyExW((UIntPtr)(ulong)hUninstall, childName, 0, KEY_READ | view, out IntPtr hEntry) != 0)
                            continue;

                        try
                        {
                            string displayName = QueryRegValueString(hEntry, "DisplayName");
                            if (!displayName.Contains("NeuralHub", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string installLoc = QueryRegValueString(hEntry, "InstallLocation");
                            if (!string.IsNullOrWhiteSpace(installLoc))
                                results.Add(installLoc);

                            string displayIcon = QueryRegValueString(hEntry, "DisplayIcon").Trim().Trim('"');
                            if (!string.IsNullOrWhiteSpace(displayIcon))
                            {
                                int comma = displayIcon.IndexOf(',');
                                if (comma > 0) displayIcon = displayIcon[..comma];
                                string? iconDir = Path.GetDirectoryName(displayIcon);
                                if (!string.IsNullOrWhiteSpace(iconDir))
                                    results.Add(iconDir);
                            }
                        }
                        finally
                        {
                            RegCloseKey(hEntry);
                        }
                    }
                }
                finally
                {
                    RegCloseKey(hUninstall);
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> FindRunningNeuralHubProcessDirs()
    {
        var dirs = new List<string>();
        try
        {
            foreach (var proc in Process.GetProcessesByName("neuralhub"))
            {
                using (proc)
                {
                    try
                    {
                        string? exe = proc.MainModule?.FileName;
                        string? dir = Path.GetDirectoryName(exe);
                        if (!string.IsNullOrWhiteSpace(dir))
                            dirs.Add(dir);
                    }
                    catch { }
                }
            }
        }
        catch { }
        return dirs;
    }

    private static string QueryRegValueString(IntPtr hKey, string valueName)
    {
        uint size = 0;
        if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out uint type, null, ref size) != 0 || size == 0)
            return string.Empty;

        byte[] data = new byte[size];
        if (RegQueryValueExW(hKey, valueName, IntPtr.Zero, out type, data, ref size) != 0)
            return string.Empty;

        if (type is REG_SZ or REG_EXPAND_SZ)
        {
            string raw = Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0');
            return type == REG_EXPAND_SZ ? Environment.ExpandEnvironmentVariables(raw) : raw;
        }
        if (type == REG_DWORD && size >= 4)
            return BitConverter.ToUInt32(data, 0).ToString();

        return string.Empty;
    }

    private static string ReadFileVersionSafe(string filePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(filePath);
            if (!string.IsNullOrWhiteSpace(info.FileVersion))
                return info.FileVersion.Split(' ')[0].Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                return info.ProductVersion.Split(' ')[0].Trim();
        }
        catch { }
        return string.Empty;
    }

    private static string ComputeSha256Upper(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static string EnsureLeadingV(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string clean = raw.Trim();
        return clean.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? clean : "v" + clean;
    }

    private static string GetJsonString(JsonElement el, string propName)
        => el.TryGetProperty(propName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? string.Empty
            : string.Empty;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyExW(UIntPtr hKey, string lpSubKey, uint ulOptions, int samDesired, out IntPtr phkResult);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out uint lpType, byte[]? lpData, ref uint lpcbData);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegEnumKeyExW(IntPtr hKey, uint dwIndex, [Out] char[] lpName, ref uint lpcchName, IntPtr lpReserved, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpftLastWriteTime);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);
}
