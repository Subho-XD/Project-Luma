using System.Diagnostics;

namespace ProjectLuma.Updater;

/// <summary>Standalone transactional self-updater supporting atomic replacement, health checks, and automatic rollback.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Contains("--verify-self-test", StringComparer.OrdinalIgnoreCase))
            return RunSelfTest();

        string? stageDir = GetArg(args, "--stage-dir");
        string? targetDir = GetArg(args, "--target-dir");
        string? exeName = GetArg(args, "--exe") ?? "ProjectLuma.exe";
        int parentPid = int.TryParse(GetArg(args, "--pid"), out int p) ? p : 0;

        if (string.IsNullOrWhiteSpace(stageDir) || string.IsNullOrWhiteSpace(targetDir) ||
            !Directory.Exists(stageDir) || !Directory.Exists(targetDir))
        {
            Console.Error.WriteLine("Usage: ProjectLuma.Updater --stage-dir <dir> --target-dir <dir> [--pid <pid>] [--exe <name>]");
            return 2;
        }

        return ApplyUpdateTransactional(stageDir, targetDir, parentPid, exeName, runHealthCheck: true);
    }

    public static int ApplyUpdateTransactional(
        string stageDir,
        string targetDir,
        int parentPid,
        string exeName,
        bool runHealthCheck)
    {
        if (parentPid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(parentPid);
                proc.WaitForExit(15000);
            }
            catch { }
        }

        string rollbackDir = Path.Combine(targetDir, ".updater_rollback");
        if (Directory.Exists(rollbackDir)) Directory.Delete(rollbackDir, true);
        Directory.CreateDirectory(rollbackDir);

        try
        {
            // Back up existing files before swapping
            foreach (string stagedFile in Directory.GetFiles(stageDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(stageDir, stagedFile);
                string destFile = Path.Combine(targetDir, rel);
                if (File.Exists(destFile))
                {
                    string bakFile = Path.Combine(rollbackDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(bakFile)!);
                    File.Copy(destFile, bakFile, overwrite: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                string tmpFile = destFile + ".update.tmp";
                File.Copy(stagedFile, tmpFile, overwrite: true);
                if (File.Exists(destFile))
                    File.Replace(tmpFile, destFile, null, ignoreMetadataErrors: true);
                else
                    File.Move(tmpFile, destFile);
            }

            if (runHealthCheck)
            {
                string launchedExe = Path.Combine(targetDir, exeName);
                if (!File.Exists(launchedExe))
                    throw new FileNotFoundException("Updated executable missing after swap.", launchedExe);

                using var healthProc = Process.Start(new ProcessStartInfo(launchedExe, "--health-check")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                if (healthProc == null || !healthProc.WaitForExit(15000) || healthProc.ExitCode != 0)
                    throw new InvalidOperationException($"Health check failed (exit code {healthProc?.ExitCode}). Rolling back update.");
            }

            Directory.Delete(rollbackDir, true);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Self-update failed: {ex.Message}");
            RestoreFromRollback(rollbackDir, targetDir);
            return 1;
        }
    }

    private static void RestoreFromRollback(string rollbackDir, string targetDir)
    {
        if (!Directory.Exists(rollbackDir)) return;
        foreach (string bakFile in Directory.GetFiles(rollbackDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(rollbackDir, bakFile);
            string dest = Path.Combine(targetDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(bakFile, dest, overwrite: true);
        }
    }

    private static int RunSelfTest()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "ProjectLuma_UpdaterTest_" + Guid.NewGuid().ToString("N")[..8]);
        string target = Path.Combine(tempRoot, "app");
        string stage = Path.Combine(tempRoot, "stage");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(stage);

        try
        {
            string appFile = Path.Combine(target, "marker.txt");
            File.WriteAllText(appFile, "v1.0.0");
            File.WriteAllText(Path.Combine(stage, "marker.txt"), "v1.1.0");

            int swapOk = ApplyUpdateTransactional(stage, target, 0, "ProjectLuma.exe", runHealthCheck: false);
            if (swapOk != 0 || File.ReadAllText(appFile) != "v1.1.0") return 10;

            // Simulate failed health check when updated executable does not exist -> must roll back to v1.1.0
            File.WriteAllText(Path.Combine(stage, "marker.txt"), "v1.2.0-broken");
            int rollbackRes = ApplyUpdateTransactional(stage, target, 0, "MissingHealthCheck.exe", runHealthCheck: true);
            if (rollbackRes == 0 || File.ReadAllText(appFile) != "v1.1.0") return 11;

            Console.WriteLine("ProjectLuma.Updater self-test passed (atomic swap + failed health-check rollback verified).");
            return 0;
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
