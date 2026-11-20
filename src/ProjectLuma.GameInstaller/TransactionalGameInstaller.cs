using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.GameInstaller;

/// <summary>Transactional installer, pre-flight guard, hash verifier, drift detector, and byte-exact rollback engine.</summary>
public sealed class TransactionalGameInstaller : IGameInstaller
{
    private const string BackupFolderName = ".luma_backup";
    private readonly IPeAnalyzer _peAnalyzer;
    private readonly ILumaLogger _logger;
    private readonly string _centralBackupMirrorDir;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    public TransactionalGameInstaller(IPeAnalyzer peAnalyzer, ILumaLogger logger, string? centralBackupMirrorDir = null)
    {
        _peAnalyzer = peAnalyzer;
        _logger = logger;
        _centralBackupMirrorDir = string.IsNullOrWhiteSpace(centralBackupMirrorDir)
            ? LumaPaths.GetSubdirectory("backups")
            : centralBackupMirrorDir;
        Directory.CreateDirectory(_centralBackupMirrorDir);
    }

    public async Task<InstallSessionManifest> InstallOrUpdateAsync(
        DiscoveredGame game,
        GameEnhancementProfile profile,
        BackendInstallPlan plan,
        CancellationToken ct = default)
    {
        ValidatePreFlight(game, profile, plan);

        string exeDir = game.ExecutableDirectory;
        string sessionId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..24];
        string backupRoot = Path.Combine(exeDir, BackupFolderName);
        string sessionDir = Path.Combine(backupRoot, sessionId);
        Directory.CreateDirectory(sessionDir);

        var manifest = new InstallSessionManifest
        {
            SessionId = sessionId,
            GameId = game.Id,
            GameTitle = game.Title,
            GameExePath = game.ExecutablePath,
            GameExeDirectory = exeDir,
            BackendId = plan.BackendId,
            BackendVersion = plan.BackendVersion,
            EnabledFeatures = new List<RenderingFeatureType>(plan.Features),
            InstalledAtUtc = DateTime.UtcNow,
            Completed = false
        };

        await SaveManifestAtomicallyAsync(sessionDir, manifest, ct);

        try
        {
            foreach (var item in plan.FilesToDeploy)
            {
                ct.ThrowIfCancellationRequested();
                string normalizedRel = item.RelativeTargetPath.TrimStart('\\', '/');
                string targetPath = Path.GetFullPath(Path.Combine(exeDir, normalizedRel));

                if (!IsInsideDirectory(targetPath, exeDir))
                    throw new InvalidOperationException($"Blocked unsafe deployment path outside game folder: {item.RelativeTargetPath}");

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                bool existedBefore = File.Exists(targetPath);

                if (existedBefore)
                {
                    string origSha = await ComputeSha256Async(targetPath, ct);
                    string backupFileName = $"{manifest.BackedUpOriginals.Count:D3}_{Path.GetFileName(targetPath)}.bak";
                    string backupFilePath = Path.Combine(sessionDir, backupFileName);
                    File.Copy(targetPath, backupFilePath, overwrite: true);
                    ClearReadOnly(backupFilePath);

                    manifest.BackedUpOriginals.Add(new BackedUpFileRecord
                    {
                        RelativePath = normalizedRel,
                        BackupFileName = backupFileName,
                        OriginalSha256 = origSha
                    });
                    await SaveManifestAtomicallyAsync(sessionDir, manifest, ct);
                }

                if (item.IsIniMerge)
                {
                    string existingIni = existedBefore ? await File.ReadAllTextAsync(targetPath, ct) : string.Empty;
                    string merged = IniMerger.MergeSections(existingIni, item.IniSections);
                    await File.WriteAllTextAsync(targetPath, merged, new UTF8Encoding(false), ct);
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(item.SourcePath) || !File.Exists(item.SourcePath))
                        throw new FileNotFoundException($"Backend payload file missing: {item.SourcePath}");

                    File.Copy(item.SourcePath, targetPath, overwrite: true);
                    ClearReadOnly(targetPath);

                    if (!string.IsNullOrWhiteSpace(item.ExpectedSha256))
                    {
                        string deployedSha = await ComputeSha256Async(targetPath, ct);
                        if (!string.Equals(deployedSha, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new IOException($"Post-copy SHA-256 verification failed for {normalizedRel}");
                    }
                }

                string finalSha = await ComputeSha256Async(targetPath, ct);
                manifest.InstalledFiles.Add(new InstalledFileRecord
                {
                    RelativePath = normalizedRel,
                    InstalledSha256 = finalSha,
                    WasMergedIni = item.IsIniMerge,
                    ExistedBefore = existedBefore
                });
                await SaveManifestAtomicallyAsync(sessionDir, manifest, ct);
            }

            manifest.Completed = true;
            await SaveManifestAtomicallyAsync(sessionDir, manifest, ct);
            MirrorSessionToCentralVault(manifest, sessionDir);

            game.InstalledBackendId = plan.BackendId;
            game.InstalledBackendVersion = plan.BackendVersion;
            game.InstalledFeatures = new List<RenderingFeatureType>(plan.Features);
            game.HealthState = IntegrationHealthState.Healthy;

            _logger.Info("GameInstaller", $"Installed {plan.BackendId} ({plan.BackendVersion}) into {game.Title} [Session {sessionId}]");
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.Error("GameInstaller", $"Installation failed for {game.Title}; executing automatic rollback", ex);
            await RollbackSingleSessionAsync(sessionDir, manifest, preserveUserModifications: false, ct);
            throw;
        }
    }

    public async Task<bool> UninstallAsync(DiscoveredGame game, CancellationToken ct = default)
    {
        EnsureGameNotRunning(game);

        string backupRoot = Path.Combine(game.ExecutableDirectory, BackupFolderName);
        if (!Directory.Exists(backupRoot)) return false;

        var sessions = await GetBackupSessionsAsync(game.ExecutableDirectory, ct);
        if (sessions.Count == 0) return false;

        bool allClean = true;
        foreach (var session in sessions)
        {
            string sessionDir = Path.Combine(backupRoot, session.SessionId);
            bool ok = await RollbackSingleSessionAsync(sessionDir, session, preserveUserModifications: true, ct);
            if (!ok) allClean = false;
        }

        if (allClean && Directory.Exists(backupRoot) && Directory.GetDirectories(backupRoot).Length == 0)
        {
            try { Directory.Delete(backupRoot, recursive: true); } catch { }
        }

        game.InstalledBackendId = string.Empty;
        game.InstalledBackendVersion = string.Empty;
        game.InstalledFeatures.Clear();
        game.HealthState = IntegrationHealthState.NotInstalled;

        _logger.Info("GameInstaller", $"Uninstalled Project Luma integration from {game.Title} (PreserveUserFiles={allClean})");
        return true;
    }

    public async Task<IntegrationHealthState> VerifyInstallationHealthAsync(DiscoveredGame game, CancellationToken ct = default)
    {
        var sessions = await GetBackupSessionsAsync(game.ExecutableDirectory, ct);
        var latest = sessions.FirstOrDefault(s => s.Completed);
        if (latest == null) return IntegrationHealthState.NotInstalled;

        foreach (var installed in latest.InstalledFiles)
        {
            string fullPath = Path.Combine(game.ExecutableDirectory, installed.RelativePath);
            if (!File.Exists(fullPath))
                return IntegrationHealthState.MissingFiles;

            string currentSha = await ComputeSha256Async(fullPath, ct);
            if (!string.Equals(currentSha, installed.InstalledSha256, StringComparison.OrdinalIgnoreCase))
                return IntegrationHealthState.DriftDetected;
        }

        return IntegrationHealthState.Healthy;
    }

    public async Task<IReadOnlyList<InstallSessionManifest>> GetBackupSessionsAsync(string gameExeDirectory, CancellationToken ct = default)
    {
        string backupRoot = Path.Combine(gameExeDirectory, BackupFolderName);
        var list = new List<InstallSessionManifest>();
        if (!Directory.Exists(backupRoot)) return list;

        foreach (string dir in Directory.GetDirectories(backupRoot).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                string json = await File.ReadAllTextAsync(manifestPath, ct);
                var m = JsonSerializer.Deserialize<InstallSessionManifest>(json, JsonOptions);
                if (m != null) list.Add(m);
            }
            catch { }
        }
        return list;
    }

    public IReadOnlyList<string> DetectRunningProcessesInDirectory(string rootDirectory)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(rootDirectory)) return found;

        string normalizedRoot = Path.GetFullPath(rootDirectory).TrimEnd('\\', '/') + "\\";
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                IntPtr h = OpenProcess(0x1000, false, proc.Id); // PROCESS_QUERY_LIMITED_INFORMATION
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    int len = sb.Capacity;
                    if (QueryFullProcessImageNameW(h, 0, sb, ref len))
                    {
                        string imgPath = sb.ToString(0, len);
                        if (imgPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                            found.Add($"{proc.ProcessName} (PID {proc.Id})");
                    }
                }
                finally
                {
                    CloseHandle(h);
                }
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
        return found;
    }

    private void ValidatePreFlight(DiscoveredGame game, GameEnhancementProfile profile, BackendInstallPlan plan)
    {
        if (string.IsNullOrWhiteSpace(game.ExecutablePath) || !File.Exists(game.ExecutablePath))
            throw new FileNotFoundException("Target game executable does not exist.", game.ExecutablePath);

        var pe = _peAnalyzer.InspectFile(game.ExecutablePath);
        if (pe == null || !pe.Is64Bit || pe.IsDll)
            throw new InvalidOperationException($"Target '{Path.GetFileName(game.ExecutablePath)}' is not a valid 64-bit Windows game executable.");

        if (game.AntiCheatStatus == AntiCheatStatus.DetectedBlocked || game.AntiCheatStatus == AntiCheatStatus.WindowsAppsProtected)
            throw new InvalidOperationException($"Installation blocked for safety: {game.AntiCheatDetails}");

        if (game.AntiCheatStatus == AntiCheatStatus.DetectedOfflineAllowed && !profile.OfflineAntiCheatConfirmed)
            throw new InvalidOperationException("Offline anti-cheat acknowledgment is required before modifying this title.");

        EnsureGameNotRunning(game);

        if (plan.FilesToDeploy.Count == 0)
            throw new InvalidOperationException("No files scheduled for deployment (verify selected features and staged backend assets).");

        // Test directory writability
        string probe = Path.Combine(game.ExecutableDirectory, $".luma_write_test_{Guid.NewGuid():N}");
        File.WriteAllBytes(probe, new byte[] { 0x4C });
        File.Delete(probe);
    }

    private void EnsureGameNotRunning(DiscoveredGame game)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in DetectRunningProcessesInDirectory(game.InstallDirectory)) running.Add(p);
        foreach (string p in DetectRunningProcessesInDirectory(game.ExecutableDirectory)) running.Add(p);
        if (running.Count > 0)
            throw new InvalidOperationException($"Game process is currently running ({string.Join(", ", running)}). Close the game before modifying files.");
    }

    private async Task<bool> RollbackSingleSessionAsync(
        string sessionDir,
        InstallSessionManifest session,
        bool preserveUserModifications,
        CancellationToken ct)
    {
        bool skippedModifiedFile = false;
        string rootExeFull = Path.GetFullPath(session.GameExeDirectory).TrimEnd('\\', '/');
        var candidateEmptyDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Remove files newly created by Project Luma only when hash matches installed state
        foreach (var installed in session.InstalledFiles.Where(f => !f.ExistedBefore))
        {
            string targetPath = Path.Combine(session.GameExeDirectory, installed.RelativePath);
            if (!File.Exists(targetPath)) continue;

            if (preserveUserModifications)
            {
                string currentSha = await ComputeSha256Async(targetPath, ct);
                if (!string.Equals(currentSha, installed.InstalledSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn("GameInstaller", $"Preserving user-modified file during uninstall: {installed.RelativePath}");
                    skippedModifiedFile = true;
                    continue;
                }
            }

            ClearReadOnly(targetPath);
            File.Delete(targetPath);

            string? dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
            while (!string.IsNullOrWhiteSpace(dir) &&
                   dir.Length > rootExeFull.Length &&
                   dir.StartsWith(rootExeFull, StringComparison.OrdinalIgnoreCase))
            {
                candidateEmptyDirs.Add(dir);
                dir = Path.GetDirectoryName(dir);
            }
        }

        // Remove empty subdirectories created during deployment (deepest first)
        foreach (string dir in candidateEmptyDirs.OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch { }
        }

        // 2. Restore original backed-up files byte-for-byte
        foreach (var orig in session.BackedUpOriginals)
        {
            string targetPath = Path.Combine(session.GameExeDirectory, orig.RelativePath);
            string backupFile = Path.Combine(sessionDir, orig.BackupFileName);
            if (!File.Exists(backupFile)) continue;

            if (preserveUserModifications && File.Exists(targetPath))
            {
                var rec = session.InstalledFiles.FirstOrDefault(f => f.RelativePath.Equals(orig.RelativePath, StringComparison.OrdinalIgnoreCase));
                if (rec != null)
                {
                    string currentSha = await ComputeSha256Async(targetPath, ct);
                    if (!string.Equals(currentSha, rec.InstalledSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Warn("GameInstaller", $"Preserving externally modified file instead of overwriting with backup: {orig.RelativePath}");
                        skippedModifiedFile = true;
                        continue;
                    }
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(backupFile, targetPath, overwrite: true);
            ClearReadOnly(targetPath);
        }

        if (!skippedModifiedFile && Directory.Exists(sessionDir))
        {
            try { Directory.Delete(sessionDir, recursive: true); } catch { }
            try
            {
                string? backupParent = Path.GetDirectoryName(sessionDir);
                if (!string.IsNullOrWhiteSpace(backupParent) &&
                    Directory.Exists(backupParent) &&
                    !Directory.EnumerateFileSystemEntries(backupParent).Any())
                {
                    Directory.Delete(backupParent, recursive: false);
                }
            }
            catch { }
        }

        return !skippedModifiedFile;
    }

    private void MirrorSessionToCentralVault(InstallSessionManifest manifest, string sessionDir)
    {
        try
        {
            string dest = Path.Combine(_centralBackupMirrorDir, SanitizeFileSegment(manifest.GameTitle), manifest.SessionId);
            Directory.CreateDirectory(dest);
            string srcManifest = Path.Combine(sessionDir, "manifest.json");
            if (File.Exists(srcManifest))
                File.Copy(srcManifest, Path.Combine(dest, "manifest.json"), overwrite: true);
        }
        catch { }
    }

    private static async Task SaveManifestAtomicallyAsync(string sessionDir, InstallSessionManifest manifest, CancellationToken ct)
    {
        string finalPath = Path.Combine(sessionDir, "manifest.json");
        string tmpPath = Path.Combine(sessionDir, "manifest.json.tmp");
        await File.WriteAllTextAsync(tmpPath, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false), ct);
        if (File.Exists(finalPath))
            File.Replace(tmpPath, finalPath, null, ignoreMetadataErrors: true);
        else
            File.Move(tmpPath, finalPath);
    }

    private static bool IsInsideDirectory(string fullPath, string parentDir)
    {
        string normalizedParent = Path.GetFullPath(parentDir).TrimEnd('\\', '/') + "\\";
        return Path.GetFullPath(fullPath).StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void ClearReadOnly(string path)
    {
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
    }

    private static string SanitizeFileSegment(string value)
        => string.Concat((value ?? "game").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
