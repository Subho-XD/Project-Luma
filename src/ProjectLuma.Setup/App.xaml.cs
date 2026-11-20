using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;

namespace ProjectLuma.Setup;

public partial class App : Application
{
    private const string EmbeddedPayloadResourceName = "ProjectLuma.Setup.payload.zip";
    private bool _isHeadlessCli;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _isHeadlessCli =
            e.Args.Contains("--verify-package", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--repair", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--capture-installer-screenshots", StringComparer.OrdinalIgnoreCase);

        RegisterGlobalExceptionHandlers();

        string? sourceOverride = GetArg(e.Args, "--source");
        string? installDirOverride = GetArg(e.Args, "--install-dir");

        if (e.Args.Contains("--verify-package", StringComparer.OrdinalIgnoreCase))
        {
            string payloadDir = ResolvePayloadDirectory(sourceOverride);
            bool valid = InstallerEngine.VerifyPayload(payloadDir, out string report);
            Console.WriteLine(report);
            Shutdown(valid ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--silent", StringComparer.OrdinalIgnoreCase))
        {
            string targetDir = ResolveInstallDirectory(installDirOverride);
            bool register = !e.Args.Contains("--no-register", StringComparer.OrdinalIgnoreCase);
            bool shortcuts = !e.Args.Contains("--no-shortcuts", StringComparer.OrdinalIgnoreCase);
            try
            {
                InstallerEngine.Install(
                    ResolvePayloadDirectory(sourceOverride),
                    targetDir,
                    createDesktopShortcut: false,
                    progress: null,
                    ct: CancellationToken.None,
                    createStartMenuShortcut: shortcuts,
                    registerInWindows: register);
                Console.WriteLine($"Project Luma v{InstallerEngine.ProductVersion} installed silently to {targetDir}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Silent install failed: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Contains("--repair", StringComparer.OrdinalIgnoreCase))
        {
            string targetDir = ResolveInstallDirectory(installDirOverride);
            bool register = !e.Args.Contains("--no-register", StringComparer.OrdinalIgnoreCase);
            try
            {
                InstallerEngine.Repair(
                    ResolvePayloadDirectory(sourceOverride),
                    targetDir,
                    registerInWindows: register);
                Console.WriteLine($"Project Luma installation repaired cleanly in {targetDir}");
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Repair failed: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        if (e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
        {
            string targetDir = ResolveInstallDirectory(installDirOverride);
            bool restoreGames = e.Args.Contains("--restore-games", StringComparer.OrdinalIgnoreCase);
            bool removePrefs = e.Args.Contains("--remove-data", StringComparer.OrdinalIgnoreCase);
            bool preserveBackups = !e.Args.Contains("--delete-backups", StringComparer.OrdinalIgnoreCase);
            try
            {
                var result = InstallerEngine.UninstallWithOptions(
                    targetDir,
                    restoreGameFiles: restoreGames,
                    preserveBackups: preserveBackups,
                    removeUserPreferencesAndCache: removePrefs,
                    removeShortcuts: true,
                    removeRegistryEntry: true);
                Console.WriteLine(result.Success ? $"Uninstalled cleanly from {targetDir}" : $"Removed installed files in {targetDir}");
                Shutdown(result.Success ? 0 : 1);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Uninstall failed: {ex.Message}");
                Shutdown(1);
            }
            return;
        }

        string? captureDir = GetArg(e.Args, "--capture-installer-screenshots");
        if (!string.IsNullOrWhiteSpace(captureDir))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var captureWindow = new SetupWindow(sourceOverride, installDirOverride)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -10000,
                Top = -10000
            };
            captureWindow.Show();
            await captureWindow.CaptureAllScreensAsync(captureDir);
            captureWindow.Close();

            var uninstallCaptureWindow = new UninstallWindow(
                ResolveInstallDirectory(installDirOverride),
                sourceOverride)
            {
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Left = -10000,
                Top = -10000
            };
            uninstallCaptureWindow.Show();
            await uninstallCaptureWindow.CaptureAllScreensAsync(captureDir);
            uninstallCaptureWindow.Close();

            Shutdown(0);
            return;
        }

        bool isUninstallerMode =
            e.Args.Contains("--uninstall-ui", StringComparer.OrdinalIgnoreCase)
            || e.Args.Contains("--maintenance", StringComparer.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(Environment.ProcessPath), InstallerEngine.UninstallerExeName, StringComparison.OrdinalIgnoreCase);

        if (isUninstallerMode)
        {
            var uninstallWindow = new UninstallWindow(
                ResolveInstallDirectory(installDirOverride),
                sourceOverride);
            MainWindow = uninstallWindow;
            uninstallWindow.Show();
            return;
        }

        // Open setup window immediately (<500ms); heavy embedded payload extraction runs asynchronously on Install.
        var win = new SetupWindow(sourceOverride, installDirOverride);
        MainWindow = win;
        win.Show();
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, exArgs) =>
        {
            WriteCrashLog("DispatcherUnhandledException", exArgs.Exception);
            if (!_isHeadlessCli)
            {
                MessageBox.Show(
                    $"Project Luma Setup encountered an unexpected error:\n\n{exArgs.Exception.Message}\n\nDetails saved to %LocalAppData%\\ProjectLuma\\logs\\setup-crash.log.",
                    $"Project Luma Setup — v{InstallerEngine.ProductVersion}",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                exArgs.Handled = true;
                Shutdown(1);
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, exArgs) =>
        {
            if (exArgs.ExceptionObject is Exception ex)
                WriteCrashLog("AppDomainUnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, exArgs) =>
        {
            WriteCrashLog("UnobservedTaskException", exArgs.Exception);
            exArgs.SetObserved();
        };
    }

    internal static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProjectLuma",
                "logs");
            Directory.CreateDirectory(logDir);
            string logFile = Path.Combine(logDir, "setup-crash.log");
            string entry = $"[{DateTime.UtcNow:O}] [{source}] v{InstallerEngine.ProductVersion}\n{ex}\n\n";
            File.AppendAllText(logFile, entry, Encoding.UTF8);
        }
        catch
        {
            // Never throw from crash logger
        }
    }

    public static bool HasAvailablePayloadSource(string? explicitSource)
    {
        if (!string.IsNullOrWhiteSpace(explicitSource) && Directory.Exists(explicitSource))
            return InstallerEngine.VerifyPayload(explicitSource, out _);

        string adjacentPayload = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(adjacentPayload) && InstallerEngine.VerifyPayload(adjacentPayload, out _))
            return true;

        if (Assembly.GetExecutingAssembly().GetManifestResourceInfo(EmbeddedPayloadResourceName) != null)
            return true;

        foreach (string devDir in GetDevPayloadCandidates())
        {
            if (Directory.Exists(devDir) && InstallerEngine.VerifyPayload(devDir, out _))
                return true;
        }

        return false;
    }

    public static string ResolveInstallDirectory(string? explicitInstallDir)
    {
        if (!string.IsNullOrWhiteSpace(explicitInstallDir))
            return Path.GetFullPath(explicitInstallDir);

        string baseDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(baseDir, "ProjectLuma.exe")) || File.Exists(Path.Combine(baseDir, "install-manifest.json")))
            return Path.GetFullPath(baseDir);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "ProjectLuma");
    }

    public static string ResolvePayloadDirectory(
        string? explicitSource,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitSource) && Directory.Exists(explicitSource))
            return explicitSource;

        string adjacentPayload = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(adjacentPayload))
            return adjacentPayload;

        string? embeddedExtracted = ExtractEmbeddedPayloadIfPresent(progress, ct);
        if (!string.IsNullOrWhiteSpace(embeddedExtracted) && Directory.Exists(embeddedExtracted))
            return embeddedExtracted;

        return GetDevPayloadCandidates().FirstOrDefault(Directory.Exists) ?? adjacentPayload;
    }

    private static string[] GetDevPayloadCandidates() => new[]
    {
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "ProjectLuma")),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "dist", "ProjectLuma")),
        Path.Combine(Directory.GetCurrentDirectory(), "dist", "ProjectLuma")
    };

    private static string? ExtractEmbeddedPayloadIfPresent(
        IProgress<(int Percent, string Message)>? progress,
        CancellationToken ct)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedPayloadResourceName);
        if (stream == null)
            return null;

        string stagingDir = Path.Combine(Path.GetTempPath(), $"ProjectLuma_Payload_{InstallerEngine.ProductVersion}");
        string readyMarker = Path.Combine(stagingDir, ".payload.ready");
        if (File.Exists(readyMarker) &&
            (File.Exists(Path.Combine(stagingDir, "ProjectLuma.exe")) || File.Exists(Path.Combine(stagingDir, "ProjectLuma.dll"))))
        {
            progress?.Report((30, "Verified cached installer payload..."));
            return stagingDir;
        }

        string tempExtractDir = stagingDir + $".tmp_{Environment.ProcessId}_{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(tempExtractDir);
            string fullRoot = Path.GetFullPath(tempExtractDir).TrimEnd('\\', '/') + "\\";
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                int total = Math.Max(1, archive.Entries.Count);
                int processed = 0;
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string outPath = Path.GetFullPath(Path.Combine(fullRoot, entry.FullName));
                    if (!outPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Blocked unsafe zip entry: {entry.FullName}");
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    entry.ExtractToFile(outPath, overwrite: true);

                    if (processed % 8 == 0 || processed == total)
                    {
                        int pct = Math.Clamp((processed * 30) / total, 1, 30);
                        progress?.Report((pct, $"Extracting installer payload ({processed}/{total})..."));
                    }
                }
            }

            File.WriteAllText(Path.Combine(tempExtractDir, ".payload.ready"), InstallerEngine.ProductVersion);

            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.Move(tempExtractDir, stagingDir);
            return stagingDir;
        }
        catch (OperationCanceledException)
        {
            try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, recursive: true); } catch { }
            throw;
        }
        catch
        {
            try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, recursive: true); } catch { }
            return null;
        }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
