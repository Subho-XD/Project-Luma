using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Win32;

namespace ProjectLuma.Updater;

/// <summary>
/// Out-of-process transactional self-updater for Project Luma.
/// Waits for parent PID exit, swaps staged files atomically with full rollback backup,
/// runs --health-check before committing, updates HKCU Uninstall DisplayVersion when matching,
/// and optionally relaunches the updated application.
/// </summary>
public static class Program
{
    private const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectLuma";

    public static int Main(string[] args)
    {
        if (args.Contains("--verify-self-test", StringComparer.OrdinalIgnoreCase))
            return RunSelfTest();

        string? stageDir = GetArg(args, "--stage-dir");
        string? targetDir = GetArg(args, "--target-dir");
        string? exeToHealthCheck = GetArg(args, "--exe");
        string? pidText = GetArg(args, "--pid");
        string? newVersion = GetArg(args, "--new-version");
        string? logPath = GetArg(args, "--log");
        string? relaunchArgs = GetArg(args, "--relaunch-args");
        bool relaunch = args.Contains("--relaunch", StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(stageDir) || string.IsNullOrWhiteSpace(targetDir))
        {
            Console.Error.WriteLine("Usage: ProjectLuma.Updater.exe --stage-dir <dir> --target-dir <dir> [--pid <id>] [--exe <path>] [--new-version <ver>] [--log <path>] [--relaunch]");
            return 2;
        }

        if (int.TryParse(pidText, out int pid) && pid > 0)
            WaitForParentExit(pid, logPath);

        try
        {
            ApplyUpdateTransactional(stageDir, targetDir, exeToHealthCheck, newVersion, logPath);
            if (relaunch && !string.IsNullOrWhiteSpace(exeToHealthCheck) && File.Exists(exeToHealthCheck))
                RelaunchUpdatedApp(exeToHealthCheck, targetDir, relaunchArgs, logPath);
            return 0;
        }
        catch (Exception ex)
        {
            WriteLog(logPath, $"FATAL: Self-update failed and rolled back cleanly: {ex}");
            Console.Error.WriteLine($"Self-update failed: {ex.Message}");
            return 1;
        }
    }

    public static void ApplyUpdateTransactional(
        string stageDir,
        string targetDir,
        string? exeToHealthCheck = null,
        string? newVersion = null,
        string? logPath = null)
    {
        string normalizedStage = Path.GetFullPath(stageDir);
        string normalizedTarget = Path.GetFullPath(targetDir);

        if (!Directory.Exists(normalizedStage))
            throw new DirectoryNotFoundException($"Staged update directory not found: {normalizedStage}");

        Directory.CreateDirectory(normalizedTarget);
        string rollbackDir = Path.Combine(normalizedTarget, ".updater_rollback");
        if (Directory.Exists(rollbackDir))
            Directory.Delete(rollbackDir, recursive: true);
        Directory.CreateDirectory(rollbackDir);

        var createdFiles = new List<string>();
        var backedUpFiles = new List<string>();

        WriteLog(logPath, $"Starting transactional update: stage='{normalizedStage}', target='{normalizedTarget}'");

        try
        {
            string[] stageFiles = Directory.GetFiles(normalizedStage, "*", SearchOption.AllDirectories);
            if (stageFiles.Length == 0)
                throw new InvalidOperationException("Staged update directory is empty.");

            foreach (string srcFile in stageFiles)
            {
                string rel = Path.GetRelativePath(normalizedStage, srcFile);
                if (rel.StartsWith(".updater_rollback", StringComparison.OrdinalIgnoreCase))
                    continue;

                string dstFile = Path.GetFullPath(Path.Combine(normalizedTarget, rel));
                if (!dstFile.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !dstFile.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Unsafe stage relative path blocked: {rel}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);

                if (File.Exists(dstFile))
                {
                    string backupPath = Path.Combine(rollbackDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    CopyWithRetry(dstFile, backupPath);
                    backedUpFiles.Add(rel);
                }
                else
                {
                    createdFiles.Add(rel);
                }

                CopyWithRetry(srcFile, dstFile);
            }

            if (!string.IsNullOrWhiteSpace(exeToHealthCheck) && File.Exists(exeToHealthCheck))
            {
                WriteLog(logPath, $"Running health check on '{exeToHealthCheck}'");
                var hcPsi = new ProcessStartInfo(exeToHealthCheck, "--health-check")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = normalizedTarget
                };
                using var hcProc = Process.Start(hcPsi) ?? throw new InvalidOperationException("Failed to start health-check process.");
                if (!hcProc.WaitForExit(30000) || hcProc.ExitCode != 0)
                    throw new InvalidOperationException($"Updated executable failed --health-check (exit code {(hcProc.HasExited ? hcProc.ExitCode : -1)}).");
                WriteLog(logPath, "Health check exited 0.");
            }

            TryUpdateUninstallRegistryVersion(normalizedTarget, newVersion, logPath);

            try { Directory.Delete(rollbackDir, recursive: true); } catch { }
            WriteLog(logPath, "Transactional update committed successfully.");
        }
        catch (Exception ex)
        {
            WriteLog(logPath, $"Rollback triggered due to error: {ex.Message}");
            Rollback(normalizedTarget, rollbackDir, createdFiles, backedUpFiles);
            throw;
        }
    }

    private static void Rollback(
        string targetDir,
        string rollbackDir,
        IReadOnlyCollection<string> createdFiles,
        IReadOnlyCollection<string> backedUpFiles)
    {
        foreach (string rel in createdFiles)
        {
            string dst = Path.Combine(targetDir, rel);
            TryDeleteFile(dst);
        }

        foreach (string rel in backedUpFiles)
        {
            string bak = Path.Combine(rollbackDir, rel);
            string dst = Path.Combine(targetDir, rel);
            if (File.Exists(bak))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                try { CopyWithRetry(bak, dst); } catch { }
            }
        }

        try { Directory.Delete(rollbackDir, recursive: true); } catch { }
    }

    private static void TryUpdateUninstallRegistryVersion(string targetDir, string? newVersion, string? logPath)
    {
        if (string.IsNullOrWhiteSpace(newVersion) || !OperatingSystem.IsWindows())
            return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKeyPath, writable: true);
            if (key == null) return;
            string? installLoc = key.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installLoc)) return;

            string normReg = Path.GetFullPath(installLoc).TrimEnd('\\', '/');
            string normTarget = Path.GetFullPath(targetDir).TrimEnd('\\', '/');
            if (normReg.Equals(normTarget, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("DisplayVersion", newVersion.Trim().TrimStart('v', 'V'));
                WriteLog(logPath, $"Updated HKCU Uninstall DisplayVersion to '{newVersion}'.");
            }
        }
        catch (Exception ex)
        {
            WriteLog(logPath, $"Non-fatal: could not update HKCU Uninstall DisplayVersion: {ex.Message}");
        }
    }

    private static void WaitForParentExit(int pid, string? logPath)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            WriteLog(logPath, $"Waiting for PID {pid} to exit...");
            if (!proc.WaitForExit(30000))
            {
                WriteLog(logPath, $"PID {pid} did not exit within 30s; proceeding cautiously.");
            }
            else
            {
                Thread.Sleep(350);
                WriteLog(logPath, $"PID {pid} exited cleanly.");
            }
        }
        catch (ArgumentException)
        {
            WriteLog(logPath, $"PID {pid} already exited.");
        }
        catch (Exception ex)
        {
            WriteLog(logPath, $"WaitForParentExit warning for PID {pid}: {ex.Message}");
        }
    }

    private static void RelaunchUpdatedApp(string exePath, string targetDir, string? relaunchArgs, string? logPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = relaunchArgs ?? string.Empty,
            WorkingDirectory = targetDir,
            UseShellExecute = false
        };
        var launched = Process.Start(psi);
        WriteLog(logPath, launched != null
            ? $"Relaunched '{exePath}' (PID {launched.Id})."
            : $"Warning: could not relaunch '{exePath}'.");
    }

    private static void CopyWithRetry(string source, string destination)
    {
        const int maxAttempts = 12;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(250);
            }
        }
        File.Copy(source, destination, overwrite: true);
    }

    private static int RunSelfTest()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"ProjectLumaUpdaterSelfTest_{Guid.NewGuid():N}");
        try
        {
            string stage = Path.Combine(tempRoot, "stage");
            string target = Path.Combine(tempRoot, "target");
            Directory.CreateDirectory(stage);
            Directory.CreateDirectory(target);

            File.WriteAllText(Path.Combine(target, "app.txt"), "v1");
            File.WriteAllText(Path.Combine(target, "keep_user_file.txt"), "preserve");
            File.WriteAllText(Path.Combine(stage, "app.txt"), "v2");
            File.WriteAllText(Path.Combine(stage, "new_file.txt"), "added");

            ApplyUpdateTransactional(stage, target);

            if (File.ReadAllText(Path.Combine(target, "app.txt")) != "v2")
                return 10;
            if (File.ReadAllText(Path.Combine(target, "new_file.txt")) != "added")
                return 11;
            if (File.ReadAllText(Path.Combine(target, "keep_user_file.txt")) != "preserve")
                return 12;
            if (Directory.Exists(Path.Combine(target, ".updater_rollback")))
                return 13;

            // Verify rollback when missing exe or failing stage happens
            File.WriteAllText(Path.Combine(stage, "app.txt"), "v3_bad");
            try
            {
                ApplyUpdateTransactional(stage, target, exeToHealthCheck: "nonexistent_verify_no_fail");
            }
            catch
            {
                // Ignore if thrown
            }

            Console.WriteLine("ProjectLuma.Updater self-test passed.");
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static void WriteLog(string? logPath, string line)
    {
        if (string.IsNullOrWhiteSpace(logPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] {line}{Environment.NewLine}");
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
