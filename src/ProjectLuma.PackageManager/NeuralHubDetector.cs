namespace ProjectLuma.PackageManager;

/// <summary>Inspects standard installation paths for an existing NeuralHub (DLSS-NR-on-AMD) runtime without modifying or repackaging upstream binaries.</summary>
public sealed class NeuralHubDetectionResult
{
    public bool IsDetected { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string InstallDirectory { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DetectionSource { get; set; } = string.Empty;
    public string Summary { get; set; } = "NeuralHub not detected on this PC.";
}

public static class NeuralHubDetector
{
    public static NeuralHubDetectionResult Detect(string? stagedBackendPath = null, IEnumerable<string>? additionalSearchRoots = null)
    {
        var candidateDirs = new List<(string Source, string Path)>();

        if (!string.IsNullOrWhiteSpace(stagedBackendPath))
            candidateDirs.Add(("Project Luma Connected Backend", stagedBackendPath));

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        candidateDirs.Add(("LocalAppData Programs", Path.Combine(localApp, "Programs", "NeuralHub")));
        candidateDirs.Add(("LocalAppData", Path.Combine(localApp, "NeuralHub")));
        candidateDirs.Add(("AppData", Path.Combine(appData, "NeuralHub")));
        candidateDirs.Add(("Program Files", Path.Combine(progFiles, "NeuralHub")));
        if (!string.IsNullOrWhiteSpace(progFilesX86))
            candidateDirs.Add(("Program Files (x86)", Path.Combine(progFilesX86, "NeuralHub")));

        if (additionalSearchRoots != null)
        {
            foreach (string extra in additionalSearchRoots.Where(d => !string.IsNullOrWhiteSpace(d)))
                candidateDirs.Add(("User Folder", extra));
        }

        foreach (var (source, dir) in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;

            try
            {
                string? exe = Directory.EnumerateFiles(dir, "NeuralHub*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase) &&
                                         !Path.GetFileName(f).Contains("unins", StringComparison.OrdinalIgnoreCase));

                string? runtimeDll = Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories)
                    .FirstOrDefault(f =>
                    {
                        string n = Path.GetFileName(f);
                        return n.Contains("dlssnr", StringComparison.OrdinalIgnoreCase) ||
                               n.Contains("neuralhub", StringComparison.OrdinalIgnoreCase) ||
                               n.Equals("dxgi.dll", StringComparison.OrdinalIgnoreCase) ||
                               n.Equals("version.dll", StringComparison.OrdinalIgnoreCase);
                    });

                if (exe != null || runtimeDll != null)
                {
                    string target = exe ?? runtimeDll!;
                    string ver = string.Empty;
                    try
                    {
                        var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(target);
                        ver = !string.IsNullOrWhiteSpace(fvi.ProductVersion) ? fvi.ProductVersion.Trim() : (fvi.FileVersion ?? string.Empty).Trim();
                    }
                    catch { }

                    return new NeuralHubDetectionResult
                    {
                        IsDetected = true,
                        ExecutablePath = exe ?? string.Empty,
                        InstallDirectory = Path.GetDirectoryName(target) ?? dir,
                        Version = ver,
                        DetectionSource = source,
                        Summary = exe != null
                            ? $"NeuralHub detected ({source}: {Path.GetDirectoryName(exe)}). Official upstream import handoff is available."
                            : $"DLSS-NR-on-AMD runtime files detected ({source}: {Path.GetDirectoryName(runtimeDll)})."
                    };
                }
            }
            catch
            {
                // Ignore inaccessible paths
            }
        }

        return new NeuralHubDetectionResult
        {
            IsDetected = false,
            Summary = "NeuralHub (DLSS-NR-on-AMD v0.9.0) was not found in standard installation folders. Download NeuralHub_v0.9.0_setup.exe from the official GitHub release or locate its installed folder."
        };
    }

    public static bool HasRuntimeBinaryPayload(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try
        {
            return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any()
                   || Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories)
                       .Any(f => !Path.GetFileName(f).Contains("setup", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
