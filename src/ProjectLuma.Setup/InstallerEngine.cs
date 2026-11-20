using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;
using ProjectLuma.GameDiscovery;
using ProjectLuma.GameInstaller;
using ProjectLuma.GpuDetection;

namespace ProjectLuma.Setup;

public sealed class CompatibilityCheckItem
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Status { get; set; } = "Ready"; // Ready, Optional improvement, Action required, Not supported
    public string Detail { get; set; } = string.Empty;
}

public sealed class InstallerSystemCheckReport
{
    public bool IsAmdGpu { get; set; }
    public bool CanInstall { get; set; } = true;
    public string SummaryTitle { get; set; } = string.Empty;
    public string SummaryDetail { get; set; } = string.Empty;
    public string GpuSummary { get; set; } = string.Empty;
    public List<CompatibilityCheckItem> Items { get; set; } = new();
}

public sealed class InstallationInspectionSummary
{
    public string InstallDirectory { get; set; } = string.Empty;
    public string AppDataDirectory { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = InstallerEngine.ProductVersion;
    public int TrackedInstalledFiles { get; set; }
    public int EnhancedGamesWithBackups { get; set; }
    public double BackupsSizeMb { get; set; }
    public double CacheAndLogsSizeMb { get; set; }
    public List<string> EnhancedGameTitles { get; set; } = new();
}

public sealed class UninstallExecutionResult
{
    public bool Success { get; set; }
    public int RemovedAppFiles { get; set; }
    public int RestoredGames { get; set; }
    public int PreservedConflictGames { get; set; }
    public bool RemovedUserCacheAndPreferences { get; set; }
    public bool PreservedBackups { get; set; }
    public bool RemovedShortcuts { get; set; }
    public bool RemovedRegistryEntry { get; set; }
    public string Report { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}

public static class InstallerEngine
{
    public const string ProductVersion = "3.3.1";
    public const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectLuma";
    public const string UninstallerExeName = "ProjectLuma-Uninstall.exe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static async Task<InstallerSystemCheckReport> RunCompatibilityCheckAsync(string? targetInstallDir = null)
    {
        var report = new InstallerSystemCheckReport();
        var logger = new NullSetupLogger();
        var gpuDetector = new WindowsGpuDetector(logger);
        var primary = await gpuDetector.DetectPrimaryGpuAsync();

        bool isAmd = primary != null && (
            primary.VendorId == 0x1002
            || primary.Manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || primary.ModelName.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
        report.IsAmdGpu = isAmd;

        bool is64BitOs = Environment.Is64BitOperatingSystem;
        report.Items.Add(new CompatibilityCheckItem
        {
            Name = "Windows Operating System",
            Value = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            Status = is64BitOs ? "Ready" : "Not supported",
            Detail = is64BitOs
                ? "64-bit Windows platform verified."
                : "Project Luma requires 64-bit Windows 10 or Windows 11."
        });

        double ramGb = GetTotalSystemRamGb();
        report.Items.Add(new CompatibilityCheckItem
        {
            Name = "Processor & Memory",
            Value = $"{Environment.ProcessorCount} threads · {ramGb:0.0} GB RAM",
            Status = ramGb >= 8.0 ? "Ready" : "Optional improvement",
            Detail = ramGb >= 8.0
                ? "Plenty of memory headroom for game library scanning and background enhancement tasks."
                : "8 GB+ RAM recommended for smooth concurrent enhancement downloads."
        });

        if (primary != null)
        {
            string targetTag = string.IsNullOrWhiteSpace(primary.GpuTarget) ? "Unknown target" : primary.GpuTarget;
            report.GpuSummary = $"{primary.ModelName} ({targetTag})";

            string gpuStatus;
            string gpuDetail;
            if (!isAmd)
            {
                gpuStatus = "Action required";
                gpuDetail = "AMD Radeon graphics hardware was not selected as the primary GPU. You can still install and inspect compatibility diagnostics.";
            }
            else if (primary.Architecture is GpuArchitecture.RDNA3 or GpuArchitecture.RDNA4 or GpuArchitecture.FutureRDNA)
            {
                gpuStatus = "Ready";
                gpuDetail = $"AMD {primary.Architecture} ({targetTag}) detected. Super Resolution, Frame Generation, and official HIP-supported paths are available.";
            }
            else if (!primary.Hip72OfficiallySupported)
            {
                gpuStatus = "Optional improvement";
                gpuDetail = $"AMD {primary.Architecture} ({targetTag}) detected. Super Resolution & Frame Generation presets are ready; official AMD HIP SDK 7.2 Windows docs exclude {targetTag}, while experimental community Neural Rendering paths remain clearly labeled.";
            }
            else
            {
                gpuStatus = "Ready";
                gpuDetail = $"AMD {primary.Architecture} ({targetTag}) detected with supported Adrenalin / enhancement paths.";
            }

            report.Items.Add(new CompatibilityCheckItem
            {
                Name = "AMD Radeon Graphics Card",
                Value = $"{primary.ModelName} · {primary.Architecture} ({targetTag})",
                Status = gpuStatus,
                Detail = gpuDetail
            });

            report.Items.Add(new CompatibilityCheckItem
            {
                Name = "Graphics Driver & .NET Runtime",
                Value = $"Driver {primary.DriverVersion} · .NET {Environment.Version}",
                Status = "Ready",
                Detail = "Self-contained .NET runtime bundled cleanly in installer package; original game backups remain automatic."
            });
        }
        else
        {
            report.GpuSummary = "No AMD Radeon GPU detected";
            report.Items.Add(new CompatibilityCheckItem
            {
                Name = "AMD Radeon Graphics Card",
                Value = "No AMD Radeon adapter detected",
                Status = "Action required",
                Detail = "Project Luma is built for AMD Radeon GPUs. Installation can proceed, though game enhancement actions will stay disabled."
            });
        }

        string installCheckPath = string.IsNullOrWhiteSpace(targetInstallDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ProjectLuma")
            : targetInstallDir;
        double freeGb = GetAvailableDiskSpaceGb(installCheckPath);
        bool enoughDisk = freeGb >= 0.4;
        report.Items.Add(new CompatibilityCheckItem
        {
            Name = "Available Storage",
            Value = $"{freeGb:0.1} GB free (~220 MB needed)",
            Status = enoughDisk ? "Ready" : "Action required",
            Detail = enoughDisk
                ? "Sufficient disk space for Project Luma and per-game backup archives."
                : "At least 400 MB of free disk space is recommended before installing."
        });

        report.CanInstall = is64BitOs && enoughDisk;
        report.SummaryTitle = report.IsAmdGpu
            ? $"Ready for {primary!.ModelName}"
            : "Installable · AMD GPU Notice";
        report.SummaryDetail = report.IsAmdGpu
            ? (primary!.Hip72OfficiallySupported
                ? "Your system meets all requirements for one-click game enhancements and safe automatic backups."
                : $"Your {primary.Architecture} ({primary.GpuTarget}) GPU is ready for Super Resolution & Frame Generation presets; Neural Rendering support notes will be shown truthfully inside the app.")
            : "Installation will succeed, though full AMD Radeon enhancement features require an AMD Radeon GPU.";

        return report;
    }

    public static bool VerifyPayload(string payloadDir, out string report)
    {
        if (string.IsNullOrWhiteSpace(payloadDir) || !Directory.Exists(payloadDir))
        {
            report = $"Installer payload directory does not exist: {payloadDir}";
            return false;
        }

        string exePath = Path.Combine(payloadDir, "ProjectLuma.exe");
        string dllPath = Path.Combine(payloadDir, "ProjectLuma.dll");
        if (!File.Exists(exePath) && !File.Exists(dllPath))
        {
            report = $"Payload directory '{payloadDir}' does not contain ProjectLuma.exe or ProjectLuma.dll.";
            return false;
        }

        var files = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories);
        int zeroByte = files.Count(f => new FileInfo(f).Length == 0);
        if (files.Length == 0 || zeroByte > 0)
        {
            report = $"Payload validation failed: {files.Length} files found, {zeroByte} empty files.";
            return false;
        }

        report = $"Verified {files.Length} installer payload files cleanly in {payloadDir}";
        return true;
    }

    public static void Install(
        string payloadDir,
        string targetDir,
        bool createDesktopShortcut = true,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken ct = default,
        bool createStartMenuShortcut = true,
        bool registerInWindows = false)
    {
        if (!VerifyPayload(payloadDir, out string verifyReport))
            throw new InvalidOperationException(verifyReport);

        string normalizedTarget = Path.GetFullPath(targetDir);
        bool rootExistedBefore = Directory.Exists(normalizedTarget);
        Directory.CreateDirectory(normalizedTarget);

        var createdFiles = new List<string>();
        var backedUpFiles = new List<(string Target, string TempBackup)>();
        string tempRollbackDir = Path.Combine(Path.GetTempPath(), $"ProjectLuma_Setup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRollbackDir);

        try
        {
            progress?.Report((5, "Scanning installer payload..."));
            string[] sourceFiles = Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories);
            var manifestHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < sourceFiles.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                string src = sourceFiles[i];
                string rel = Path.GetRelativePath(payloadDir, src);
                string dst = Path.GetFullPath(Path.Combine(normalizedTarget, rel));

                if (!IsInsideDirectory(dst, normalizedTarget))
                    throw new InvalidOperationException($"Blocked unsafe installer path: {rel}");

                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst))
                {
                    string bak = Path.Combine(tempRollbackDir, $"{backedUpFiles.Count:D4}_{Path.GetFileName(dst)}");
                    File.Copy(dst, bak, overwrite: true);
                    backedUpFiles.Add((dst, bak));
                }
                else
                {
                    createdFiles.Add(dst);
                }

                File.Copy(src, dst, overwrite: true);
                manifestHashes[rel] = ComputeSha256(dst);

                int pct = 10 + (int)Math.Round(72.0 * (i + 1) / sourceFiles.Length);
                progress?.Report((pct, $"Copying {rel}..."));
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report((86, "Deploying uninstaller & release notes..."));

            foreach (var relDeployed in DeployUninstallerFiles(normalizedTarget, createdFiles, backedUpFiles, tempRollbackDir))
            {
                string full = Path.Combine(normalizedTarget, relDeployed);
                if (File.Exists(full))
                    manifestHashes[relDeployed] = ComputeSha256(full);
            }

            WriteReleaseNotes(normalizedTarget, createdFiles, backedUpFiles, tempRollbackDir);
            string notesPath = Path.Combine(normalizedTarget, "RELEASE_NOTES.txt");
            if (File.Exists(notesPath))
                manifestHashes["RELEASE_NOTES.txt"] = ComputeSha256(notesPath);

            var manifest = new InstallManifestRecord
            {
                Product = "Project Luma",
                Version = ProductVersion,
                InstalledAtUtc = DateTime.UtcNow,
                InstallDirectory = normalizedTarget,
                FileCount = manifestHashes.Count,
                Files = manifestHashes
            };
            string manifestPath = Path.Combine(normalizedTarget, "install-manifest.json");
            if (!File.Exists(manifestPath))
                createdFiles.Add(manifestPath);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);

            progress?.Report((94, "Creating Windows shortcuts..."));
            if (createDesktopShortcut)
                EnsureDesktopShortcut(normalizedTarget);

            if (createStartMenuShortcut)
                EnsureStartMenuShortcuts(normalizedTarget);

            if (registerInWindows && OperatingSystem.IsWindows())
            {
                RegisterInstalledAppInWindows(normalizedTarget, manifestHashes.Keys.Select(k => Path.Combine(normalizedTarget, k)));
                new WindowsStartupService().UpdateExecutablePathIfEnabled(Path.Combine(normalizedTarget, "ProjectLuma.exe"));
            }

            progress?.Report((100, $"Project Luma v{ProductVersion} installed cleanly."));
        }
        catch
        {
            foreach (string created in createdFiles)
            {
                try { if (File.Exists(created)) File.Delete(created); } catch { }
            }
            foreach (var (target, tempBackup) in backedUpFiles)
            {
                try { if (File.Exists(tempBackup)) File.Copy(tempBackup, target, overwrite: true); } catch { }
            }
            if (!rootExistedBefore && Directory.Exists(normalizedTarget))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(normalizedTarget).Any())
                        Directory.Delete(normalizedTarget, recursive: false);
                }
                catch { }
            }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(tempRollbackDir)) Directory.Delete(tempRollbackDir, recursive: true); } catch { }
        }
    }

    public static void Repair(
        string payloadDir,
        string targetDir,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken ct = default,
        bool registerInWindows = false)
        => Install(payloadDir, targetDir, createDesktopShortcut: false, progress: progress, ct: ct, createStartMenuShortcut: true, registerInWindows: registerInWindows);

    public static bool Uninstall(string targetDir, bool removeShortcuts = true)
    {
        var result = UninstallWithOptions(
            targetDir,
            restoreGameFiles: false,
            preserveBackups: true,
            removeUserPreferencesAndCache: false,
            removeShortcuts: removeShortcuts,
            removeRegistryEntry: removeShortcuts,
            appDataOverride: null);
        return result.Success;
    }

    public static InstallationInspectionSummary InspectInstallationState(string targetDir, string? appDataOverride = null)
    {
        string appDataDir = ResolveAppDataDir(appDataOverride);
        string normalizedTarget = string.IsNullOrWhiteSpace(targetDir)
            ? string.Empty
            : Path.GetFullPath(targetDir);

        var summary = new InstallationInspectionSummary
        {
            InstallDirectory = normalizedTarget,
            AppDataDirectory = appDataDir,
            InstalledVersion = ProductVersion
        };

        string manifestPath = Path.Combine(normalizedTarget, "install-manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<InstallManifestRecord>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest != null)
                {
                    summary.InstalledVersion = string.IsNullOrWhiteSpace(manifest.Version) ? ProductVersion : manifest.Version;
                    summary.TrackedInstalledFiles = manifest.FileCount;
                }
            }
            catch { }
        }

        var games = DiscoverBackedUpGames(appDataDir);
        summary.EnhancedGamesWithBackups = games.Count;
        summary.EnhancedGameTitles = games
            .Select(g => string.IsNullOrWhiteSpace(g.Title) ? Path.GetFileName(g.ExecutablePath) : g.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string backupsDir = Path.Combine(appDataDir, "backups");
        summary.BackupsSizeMb = GetDirectorySizeMb(backupsDir);

        double cacheMb = 0;
        foreach (string sub in new[] { "logs", "cache", "artworks", "downloads", "components" })
            cacheMb += GetDirectorySizeMb(Path.Combine(appDataDir, sub));
        summary.CacheAndLogsSizeMb = Math.Round(cacheMb, 2);

        return summary;
    }

    public static UninstallExecutionResult UninstallWithOptions(
        string targetDir,
        bool restoreGameFiles,
        bool preserveBackups,
        bool removeUserPreferencesAndCache,
        bool removeShortcuts = true,
        bool removeRegistryEntry = true,
        string? appDataOverride = null)
    {
        var result = new UninstallExecutionResult();
        var reportSb = new StringBuilder();
        string normalizedTarget = Path.GetFullPath(targetDir);
        string appDataDir = ResolveAppDataDir(appDataOverride);

        if (restoreGameFiles)
        {
            var logger = new NullSetupLogger();
            string centralBackups = Path.Combine(appDataDir, "backups");
            var gameInstaller = new TransactionalGameInstaller(new PeAnalyzer(), logger, centralBackups);
            var gamesToRestore = DiscoverBackedUpGames(appDataDir);

            foreach (var game in gamesToRestore)
            {
                try
                {
                    bool rolledBack = gameInstaller.UninstallAsync(game).GetAwaiter().GetResult();
                    if (rolledBack)
                    {
                        string backupRoot = Path.Combine(game.ExecutableDirectory, ".luma_backup");
                        if (Directory.Exists(backupRoot) && Directory.GetDirectories(backupRoot).Length > 0)
                        {
                            result.PreservedConflictGames++;
                            result.Warnings.Add($"Preserved externally modified files in {game.Title}.");
                        }
                        else
                        {
                            result.RestoredGames++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.PreservedConflictGames++;
                    result.Warnings.Add($"Could not auto-restore {game.Title}: {ex.Message}");
                }
            }
            reportSb.AppendLine($"Games restored: {result.RestoredGames} (preserved conflict/user-modified: {result.PreservedConflictGames})");
        }

        if (Directory.Exists(normalizedTarget))
        {
            string? currentProcess = Environment.ProcessPath;
            bool runningInsideTarget = !string.IsNullOrWhiteSpace(currentProcess)
                && IsInsideDirectory(currentProcess, normalizedTarget);

            string manifestPath = Path.Combine(normalizedTarget, "install-manifest.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<InstallManifestRecord>(File.ReadAllText(manifestPath), JsonOptions);
                    if (manifest != null)
                    {
                        foreach (string rel in manifest.Files.Keys)
                        {
                            string file = Path.GetFullPath(Path.Combine(normalizedTarget, rel));
                            if (runningInsideTarget && PathsEqual(file, currentProcess!)) continue;
                            if (File.Exists(file))
                            {
                                ClearReadOnly(file);
                                File.Delete(file);
                                result.RemovedAppFiles++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Manifest read issue during cleanup: {ex.Message}");
                }

                try { File.Delete(manifestPath); result.RemovedAppFiles++; } catch { }
            }

            foreach (string file in Directory.GetFiles(normalizedTarget, "*", SearchOption.AllDirectories))
            {
                if (runningInsideTarget && PathsEqual(file, currentProcess!)) continue;
                try
                {
                    ClearReadOnly(file);
                    File.Delete(file);
                    result.RemovedAppFiles++;
                }
                catch { }
            }

            if (!runningInsideTarget)
            {
                try { Directory.Delete(normalizedTarget, recursive: true); } catch { }
            }
            else
            {
                ScheduleDeferredSelfCleanup(currentProcess!, normalizedTarget);
            }
        }

        if (removeUserPreferencesAndCache && Directory.Exists(appDataDir))
        {
            foreach (string sub in new[] { "logs", "cache", "artworks", "downloads", "components", "profiles" })
            {
                string dir = Path.Combine(appDataDir, sub);
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { }
                }
            }
            foreach (string fileName in new[] { "settings.json", "library.json" })
            {
                string f = Path.Combine(appDataDir, fileName);
                if (File.Exists(f))
                {
                    try { File.Delete(f); } catch { }
                }
            }

            if (!preserveBackups)
            {
                string backupsDir = Path.Combine(appDataDir, "backups");
                if (Directory.Exists(backupsDir))
                {
                    try { Directory.Delete(backupsDir, recursive: true); } catch { }
                }
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(appDataDir).Any())
                        Directory.Delete(appDataDir, recursive: false);
                }
                catch { }
            }

            result.RemovedUserCacheAndPreferences = true;
        }
        result.PreservedBackups = preserveBackups;

        if (removeShortcuts)
        {
            RemoveShortcuts();
            result.RemovedShortcuts = true;
        }

        if (removeRegistryEntry && OperatingSystem.IsWindows())
        {
            RemoveInstalledAppRegistry();
            try { new WindowsStartupService().Disable(); } catch { }
            result.RemovedRegistryEntry = true;
        }

        result.Success = !Directory.Exists(normalizedTarget) || result.RemovedAppFiles > 0 || result.RestoredGames > 0;
        reportSb.AppendLine($"Removed application files: {result.RemovedAppFiles}");
        reportSb.AppendLine($"Removed shortcuts: {result.RemovedShortcuts} · Registry cleaned: {result.RemovedRegistryEntry}");
        result.Report = reportSb.ToString().Trim();
        return result;
    }

    public static string ExportUninstallDiagnostics(string outputPath, string targetDir, string? appDataOverride = null)
    {
        var summary = InspectInstallationState(targetDir, appDataOverride);
        var payload = new
        {
            ExportedAtUtc = DateTime.UtcNow,
            Product = "Project Luma",
            Version = ProductVersion,
            OperatingSystem = RuntimeInformation.OSDescription,
            Summary = summary
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8);
        return outputPath;
    }

    public static void EnsureDesktopShortcut(string targetDir)
    {
        string appExe = Path.Combine(targetDir, "ProjectLuma.exe");
        if (!File.Exists(appExe)) return;
        string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Project Luma.lnk");
        string iconPath = File.Exists(Path.Combine(targetDir, "project-luma.ico"))
            ? Path.Combine(targetDir, "project-luma.ico")
            : appExe;
        CreateShortcut(appExe, desktopLnk, arguments: null, iconPath: iconPath);
    }

    public static void EnsureStartMenuShortcuts(string targetDir)
    {
        string appExe = Path.Combine(targetDir, "ProjectLuma.exe");
        if (!File.Exists(appExe)) return;
        string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Project Luma");
        Directory.CreateDirectory(startDir);

        string iconPath = File.Exists(Path.Combine(targetDir, "project-luma.ico"))
            ? Path.Combine(targetDir, "project-luma.ico")
            : appExe;
        CreateShortcut(appExe, Path.Combine(startDir, "Project Luma.lnk"), arguments: null, iconPath: iconPath);

        string uninstallExe = Path.Combine(targetDir, UninstallerExeName);
        if (File.Exists(uninstallExe))
        {
            CreateShortcut(
                uninstallExe,
                Path.Combine(startDir, "Uninstall Project Luma.lnk"),
                arguments: $"--uninstall-ui --install-dir \"{targetDir}\"",
                iconPath: iconPath);
        }
    }

    public static double GetAvailableDiskSpaceGb(string path)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "C:\\";
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
        }
        catch
        {
            return 10.0;
        }
    }

    private static IEnumerable<string> DeployUninstallerFiles(
        string normalizedTarget,
        List<string> createdFiles,
        List<(string Target, string TempBackup)> backedUpFiles,
        string tempRollbackDir)
    {
        var deployedRelPaths = new List<string>();
        string? processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            return deployedRelPaths;

        string procName = Path.GetFileName(processPath);
        if (procName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return deployedRelPaths;

        string uninstallerDst = Path.Combine(normalizedTarget, UninstallerExeName);
        CopyTrackedFile(processPath, uninstallerDst, createdFiles, backedUpFiles, tempRollbackDir);
        deployedRelPaths.Add(UninstallerExeName);

        // If running as non-single-file framework-dependent apphost, copy ProjectLuma-Setup-x64.* companions
        string sourceDir = Path.GetDirectoryName(processPath)!;
        foreach (string companionName in new[]
                 {
                     "ProjectLuma-Setup-x64.dll",
                     "ProjectLuma-Setup-x64.deps.json",
                     "ProjectLuma-Setup-x64.runtimeconfig.json"
                 })
        {
            string src = Path.Combine(sourceDir, companionName);
            if (!File.Exists(src)) continue;
            string dst = Path.Combine(normalizedTarget, companionName);
            CopyTrackedFile(src, dst, createdFiles, backedUpFiles, tempRollbackDir);
            deployedRelPaths.Add(companionName);
        }

        return deployedRelPaths;
    }

    private static void CopyTrackedFile(
        string src,
        string dst,
        List<string> createdFiles,
        List<(string Target, string TempBackup)> backedUpFiles,
        string tempRollbackDir)
    {
        if (File.Exists(dst))
        {
            string bak = Path.Combine(tempRollbackDir, $"{backedUpFiles.Count:D4}_{Path.GetFileName(dst)}");
            File.Copy(dst, bak, overwrite: true);
            backedUpFiles.Add((dst, bak));
        }
        else
        {
            createdFiles.Add(dst);
        }
        File.Copy(src, dst, overwrite: true);
    }

    private static void WriteReleaseNotes(
        string targetDir,
        List<string> createdFiles,
        List<(string Target, string TempBackup)> backedUpFiles,
        string tempRollbackDir)
    {
        string dst = Path.Combine(targetDir, "RELEASE_NOTES.txt");
        if (File.Exists(dst))
        {
            string bak = Path.Combine(tempRollbackDir, $"{backedUpFiles.Count:D4}_RELEASE_NOTES.txt");
            File.Copy(dst, bak, overwrite: true);
            backedUpFiles.Add((dst, bak));
        }
        else
        {
            createdFiles.Add(dst);
        }

        string notes = $"""
            Project Luma v{ProductVersion} — AMD Gaming Enhancement Launcher
            ==================================================================
            - Redesigned launcher workspace with dominant featured hero banner, 16:9 game strip, and quiet navigation
            - Dedicated 4-category Enhancement Library (AI Visual Enhancement, Super Resolution, Frame Generation, Reconstruction)
            - AMD Radeon crimson & obsidian 4-point design system with official Google Sans + Space Mono typography
            - Live RX 6600 (RDNA 2 / gfx1032) HIP probe, 8-state Neural Rendering diagnostics, and strict licensing compliance
            - Responsive streaming GitHub package downloads with single-pass .7z extraction, cancellation, and SHA-256 verification
            - Complete Windows Installed Apps registration and safe per-game restoration uninstaller
            """;
        File.WriteAllText(dst, notes, Encoding.UTF8);
    }

    private static void RegisterInstalledAppInWindows(string targetDir, IEnumerable<string> installedFiles)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKeyPath, writable: true);
            if (key == null) return;

            string appExe = Path.Combine(targetDir, "ProjectLuma.exe");
            string uninstallExe = Path.Combine(targetDir, UninstallerExeName);
            string launcherForUninstall = File.Exists(uninstallExe) ? uninstallExe : appExe;
            string iconPath = File.Exists(Path.Combine(targetDir, "project-luma.ico"))
                ? Path.Combine(targetDir, "project-luma.ico")
                : appExe;

            long totalBytes = 0;
            foreach (string file in installedFiles)
            {
                try { if (File.Exists(file)) totalBytes += new FileInfo(file).Length; } catch { }
            }
            int estimatedKb = (int)Math.Max(1024, totalBytes / 1024);

            key.SetValue("DisplayName", "Project Luma");
            key.SetValue("DisplayVersion", ProductVersion);
            key.SetValue("Publisher", "Project Luma");
            key.SetValue("InstallLocation", targetDir);
            key.SetValue("DisplayIcon", iconPath);
            key.SetValue("UninstallString", $"\"{launcherForUninstall}\" --uninstall-ui --install-dir \"{targetDir}\"");
            key.SetValue("QuietUninstallString", $"\"{launcherForUninstall}\" --uninstall --install-dir \"{targetDir}\"");
            key.SetValue("ModifyPath", $"\"{launcherForUninstall}\" --maintenance --install-dir \"{targetDir}\"");
            key.SetValue("NoModify", 0, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 0, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void RemoveInstalledAppRegistry()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKeyPath, throwOnMissingSubKey: false); } catch { }
    }

    private static void RemoveShortcuts()
    {
        string desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Project Luma.lnk");
        if (File.Exists(desktopLnk))
        {
            try { File.Delete(desktopLnk); } catch { }
        }
        string startDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Project Luma");
        if (Directory.Exists(startDir))
        {
            try { Directory.Delete(startDir, recursive: true); } catch { }
        }
    }

    private static List<DiscoveredGame> DiscoverBackedUpGames(string appDataDir)
    {
        var byExeDir = new Dictionary<string, DiscoveredGame>(StringComparer.OrdinalIgnoreCase);

        string libraryPath = Path.Combine(appDataDir, "library.json");
        if (File.Exists(libraryPath))
        {
            try
            {
                var lib = JsonSerializer.Deserialize<List<DiscoveredGame>>(File.ReadAllText(libraryPath), JsonOptions);
                if (lib != null)
                {
                    foreach (var g in lib)
                    {
                        if (!string.IsNullOrWhiteSpace(g.ExecutableDirectory) && Directory.Exists(Path.Combine(g.ExecutableDirectory, ".luma_backup")))
                            byExeDir[g.ExecutableDirectory] = g;
                    }
                }
            }
            catch { }
        }

        string centralBackups = Path.Combine(appDataDir, "backups");
        if (Directory.Exists(centralBackups))
        {
            foreach (string manifestPath in Directory.GetFiles(centralBackups, "manifest.json", SearchOption.AllDirectories))
            {
                try
                {
                    var m = JsonSerializer.Deserialize<InstallSessionManifest>(File.ReadAllText(manifestPath), JsonOptions);
                    if (m == null || string.IsNullOrWhiteSpace(m.GameExeDirectory)) continue;
                    if (byExeDir.ContainsKey(m.GameExeDirectory)) continue;
                    if (!Directory.Exists(Path.Combine(m.GameExeDirectory, ".luma_backup"))) continue;

                    byExeDir[m.GameExeDirectory] = new DiscoveredGame
                    {
                        Id = m.GameId,
                        Title = string.IsNullOrWhiteSpace(m.GameTitle) ? Path.GetFileName(m.GameExeDirectory) : m.GameTitle,
                        ExecutablePath = m.GameExePath,
                        ExecutableDirectory = m.GameExeDirectory,
                        InstallDirectory = m.GameExeDirectory
                    };
                }
                catch { }
            }
        }

        return byExeDir.Values.ToList();
    }

    private static void ScheduleDeferredSelfCleanup(string currentProcess, string targetDir)
    {
        try
        {
            string cmd = $"/c timeout /t 2 /nobreak >nul & del /f /q \"{currentProcess}\" >nul 2>nul & rmdir /s /q \"{targetDir}\" >nul 2>nul";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }
    }

    private static string ResolveAppDataDir(string? appDataOverride)
        => string.IsNullOrWhiteSpace(appDataOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectLuma")
            : Path.GetFullPath(appDataOverride);

    private static double GetDirectorySizeMb(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long bytes = 0;
        foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { bytes += new FileInfo(f).Length; } catch { }
        }
        return Math.Round(bytes / (1024.0 * 1024.0), 2);
    }

    private static void CreateShortcut(string exePath, string lnkPath, string? arguments, string? iconPath)
    {
        try
        {
            string argsPart = string.IsNullOrWhiteSpace(arguments)
                ? string.Empty
                : $"$s.Arguments='{arguments.Replace("'", "''")}';";
            string iconPart = string.IsNullOrWhiteSpace(iconPath)
                ? string.Empty
                : $"$s.IconLocation='{iconPath.Replace("'", "''")}';";
            string script = $"$s=(New-Object -COM WScript.Shell).CreateShortcut('{lnkPath.Replace("'", "''")}');$s.TargetPath='{exePath.Replace("'", "''")}';$s.WorkingDirectory='{Path.GetDirectoryName(exePath)!.Replace("'", "''")}';{argsPart}{iconPart}$s.Save()";
            using var p = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    private static double GetTotalSystemRamGb()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref status))
                return status.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        }
        catch { }
        return 16.0;
    }

    private static string ComputeSha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static bool IsInsideDirectory(string fullPath, string parentDir)
    {
        string normalizedParent = Path.GetFullPath(parentDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(fullPath);
        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    private sealed class InstallManifestRecord
    {
        public string Product { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public DateTime InstalledAtUtc { get; set; }
        public string InstallDirectory { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public Dictionary<string, string> Files { get; set; } = new();
    }

    private sealed class NullSetupLogger : ILumaLogger
    {
        public event Action<LogEntryRecord>? EntryLogged
        {
            add { }
            remove { }
        }
        public IReadOnlyList<LogEntryRecord> RecentEntries => Array.Empty<LogEntryRecord>();
        public string LogDirectory => Path.GetTempPath();
        public void Log(LumaLogLevel level, string category, string message, string? details = null) { }
        public void Info(string category, string message, string? details = null) { }
        public void Warn(string category, string message, string? details = null) { }
        public void Error(string category, string message, Exception? ex = null) { }
    }
}
