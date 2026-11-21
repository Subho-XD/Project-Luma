using System.Diagnostics;
using System.Text.RegularExpressions;
using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Interfaces;
using ProjectLuma.Core.Models;

namespace ProjectLuma.GameDiscovery;

/// <summary>Inspects an installation directory to identify the primary shipping executable, engine, rendering API, native upscalers, and anti-cheat.</summary>
public sealed class GameDirectoryInspector
{
    private readonly IPeAnalyzer _peAnalyzer;

    private static readonly string[] IgnoredFolders =
    {
        "_storage_", ".luma_backup", "amdlss5_backup", "reframework", "_commonredist", "commonredist", "redist",
        "redistributables", "__installer", "directx", "vcredist", "support", "crashreporter", "easyanticheat",
        "battleye", ".git", "localization", "movies", "archive", "red4ext"
    };

    private static readonly string[] IgnoredExeSubstrings =
    {
        "crash", "unins", "setup", "redist", "launcher", "report", "install", "vc_", "dxsetup",
        "dotnet", "ue4prereq", "easyanticheat", "be_service", "cefprocess", "helper", "unitycrashhandler",
        "webhelper", "repair", "cleaner", "analyzer", "dump"
    };

    public GameDirectoryInspector(IPeAnalyzer peAnalyzer)
    {
        _peAnalyzer = peAnalyzer;
    }

    public DiscoveredGame? Inspect(string pathOrExe, string title, GameLauncherType launcher, string storeAppId)
    {
        string? exePath = null;
        string installRoot;

        if (File.Exists(pathOrExe) && pathOrExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            exePath = Path.GetFullPath(pathOrExe);
            installRoot = ResolveInstallRootForExecutable(exePath);
        }
        else if (Directory.Exists(pathOrExe))
        {
            installRoot = Path.GetFullPath(pathOrExe);
            exePath = FindBestExecutable(installRoot);
        }
        else
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return null;

        string exeDir = Path.GetDirectoryName(exePath)!;
        var pe = _peAnalyzer.InspectFile(exePath);
        if (File.Exists(pathOrExe) && pathOrExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            (pe == null || pe.IsDll))
        {
            return null;
        }

        bool isX64 = pe?.Is64Bit ?? false;

        string resolvedTitle = title;
        if (string.IsNullOrWhiteSpace(resolvedTitle))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exePath);
                resolvedTitle = !string.IsNullOrWhiteSpace(vi.ProductName)
                    ? vi.ProductName.Trim()
                    : Path.GetFileNameWithoutExtension(exePath);
            }
            catch
            {
                resolvedTitle = Path.GetFileNameWithoutExtension(exePath);
            }
        }

        var game = new DiscoveredGame
        {
            Id = $"{launcher.ToString().ToLowerInvariant()}:{(string.IsNullOrWhiteSpace(storeAppId) ? Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant() : storeAppId)}:{Math.Abs(installRoot.ToLowerInvariant().GetHashCode()):X8}",
            Title = resolvedTitle,
            ExecutablePath = exePath,
            ExecutableDirectory = exeDir,
            InstallDirectory = installRoot,
            Launcher = launcher,
            StoreAppId = storeAppId,
            Is64Bit = isX64,
            ImportsVersionDll = pe?.ImportsDll("version.dll") ?? false,
            ImportsDxgiDll = pe?.ImportsDll("dxgi.dll") ?? false,
            LastScannedUtc = DateTime.UtcNow
        };

        // Check indirect version.dll dependency via adjacent game DLLs (e.g., Witcher 3 xaudio2_9redist.dll)
        if (!game.ImportsVersionDll && pe != null)
        {
            foreach (string imported in pe.ImportedDlls)
            {
                string localDll = Path.Combine(exeDir, imported);
                if (!File.Exists(localDll)) continue;
                var depPe = _peAnalyzer.InspectFile(localDll);
                if (depPe is { Is64Bit: true } && depPe.ImportsDll("version.dll"))
                {
                    game.ImportsVersionDll = true;
                    break;
                }
            }
        }

        DetectGraphicsApiAndUpscalers(game, pe);
        game.EngineName = DetectEngine(installRoot, exeDir, exePath);

        var (acStatus, acDetails) = AntiCheatDetector.Evaluate(exeDir, installRoot);
        game.AntiCheatStatus = acStatus;
        game.AntiCheatDetails = acDetails;

        return game;
    }

    private string? FindBestExecutable(string rootDir)
    {
        var candidates = new List<(string Path, int Depth)>();
        CollectExecutables(rootDir, 0, candidates);

        string? best = null;
        long bestScore = long.MinValue;

        foreach (var (path, depth) in candidates)
        {
            string lowerName = Path.GetFileName(path).ToLowerInvariant();
            if (IgnoredExeSubstrings.Any(s => lowerName.Contains(s))) continue;

            var pe = _peAnalyzer.InspectFile(path);
            if (pe == null || pe.IsDll) continue;

            long score = 0;
            if (pe.Is64Bit) score += 200;
            string rootFolderName = Path.GetFileName(rootDir.TrimEnd('\\', '/')).ToLowerInvariant();
            string exeBaseName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(rootFolderName) && exeBaseName.Equals(rootFolderName, StringComparison.OrdinalIgnoreCase))
                score += 150;
            if (pe.ImportsDll("version.dll")) score += 80;
            if (pe.ImportsDll("d3d12.dll")) score += 40;
            if (pe.ImportsDll("dxgi.dll")) score += 20;
            if (lowerName.EndsWith("-win64-shipping.exe")) score += 120;

            string dir = Path.GetDirectoryName(path)!;
            if (File.Exists(Path.Combine(dir, "amd_fidelityfx_dx12.dll")) ||
                File.Exists(Path.Combine(dir, "amd_fidelityfx_upscaler_dx12.dll")) ||
                File.Exists(Path.Combine(dir, "nvngx_dlss.dll")))
            {
                score += 60;
            }

            try
            {
                long sizeMb = new FileInfo(path).Length / (1024 * 1024);
                score += Math.Min(sizeMb / 10, 25);
            }
            catch { }

            score -= depth * 5;
            if (score > bestScore)
            {
                bestScore = score;
                best = path;
            }
        }

        return best;
    }

    private static string ResolveInstallRootForExecutable(string exePath)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        try
        {
            string? cur = exeDir;
            for (int depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(cur); depth++)
            {
                if (Directory.Exists(Path.Combine(cur, "Engine")))
                    return cur;
                cur = Directory.GetParent(cur)?.FullName;
            }

            if (exeDir.EndsWith(Path.Combine("bin", "x64"), StringComparison.OrdinalIgnoreCase) ||
                exeDir.EndsWith(Path.Combine("Binaries", "Win64"), StringComparison.OrdinalIgnoreCase) ||
                exeDir.EndsWith(Path.Combine("Binaries", "WinGDK"), StringComparison.OrdinalIgnoreCase))
            {
                string? candidate = Directory.GetParent(exeDir)?.Parent?.FullName;
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                    return candidate;
            }
        }
        catch
        {
            // Keep executable directory fallback cleanly
        }
        return exeDir;
    }

    private static void CollectExecutables(string dir, int depth, List<(string Path, int Depth)> output)
    {
        if (depth > 3) return;
        try
        {
            foreach (string exe in Directory.GetFiles(dir, "*.exe"))
                output.Add((exe, depth));

            foreach (string sub in Directory.GetDirectories(dir))
            {
                string folder = Path.GetFileName(sub).ToLowerInvariant();
                if (IgnoredFolders.Contains(folder)) continue;
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                CollectExecutables(sub, depth + 1, output);
            }
        }
        catch
        {
            // Ignore restricted folders
        }
    }

    private static void DetectGraphicsApiAndUpscalers(DiscoveredGame game, PeInspectionResult? pe)
    {
        bool hasDx12Import = pe?.ImportsDll("d3d12.dll") ?? false;
        bool hasVulkanImport = pe?.ImportsDll("vulkan-1.dll") ?? false;
        bool hasDx11Import = pe?.ImportsDll("d3d11.dll") ?? false;

        string exeDir = game.ExecutableDirectory;
        string rootDir = game.InstallDirectory;

        var nearbyFiles = new List<string>();
        CollectNearbyDlls(exeDir, 0, 2, nearbyFiles);
        if (!string.Equals(exeDir, rootDir, StringComparison.OrdinalIgnoreCase))
            CollectNearbyDlls(rootDir, 0, 2, nearbyFiles);

        string uePluginsDir = Path.Combine(rootDir, "Engine", "Plugins", "Runtime");
        if (Directory.Exists(uePluginsDir))
            CollectNearbyDlls(uePluginsDir, 0, 5, nearbyFiles);

        bool hasD3D12Core = nearbyFiles.Any(f => f.EndsWith("D3D12Core.dll", StringComparison.OrdinalIgnoreCase));
        bool hasFfxDx12 = nearbyFiles.Any(f =>
            f.EndsWith("amd_fidelityfx_dx12.dll", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith("amd_fidelityfx_upscaler_dx12.dll", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith("amd_fidelityfx_loader_dx12.dll", StringComparison.OrdinalIgnoreCase));

        if (hasDx12Import || hasD3D12Core || hasFfxDx12 || (pe?.ImportsDll("dxgi.dll") == true && !hasDx11Import && !hasVulkanImport))
            game.GraphicsApi = GraphicsApi.DirectX12;
        else if (hasVulkanImport)
            game.GraphicsApi = GraphicsApi.Vulkan;
        else if (hasDx11Import)
            game.GraphicsApi = GraphicsApi.DirectX11;
        else if (Directory.Exists(Path.Combine(rootDir, "Engine")) || exeDir.Contains("Win64", StringComparison.OrdinalIgnoreCase))
            game.GraphicsApi = GraphicsApi.DirectX12;

        // Detect native upscalers & FG DLLs
        game.NativeDlssSupported = nearbyFiles.Any(f => Path.GetFileName(f).Equals("nvngx_dlss.dll", StringComparison.OrdinalIgnoreCase));
        game.NativeRayReconstructionSupported = nearbyFiles.Any(f => Path.GetFileName(f).Equals("nvngx_dlssd.dll", StringComparison.OrdinalIgnoreCase));
        game.NativeXeSsSupported = nearbyFiles.Any(f => Path.GetFileName(f).Equals("libxess.dll", StringComparison.OrdinalIgnoreCase));

        string? fsrDll = nearbyFiles.FirstOrDefault(f =>
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            return n is "amd_fidelityfx_dx12.dll" or "amd_fidelityfx_upscaler_dx12.dll" ||
                   n.StartsWith("ffx_fsr3") || n.StartsWith("ffx_fsr2");
        });

        if (fsrDll != null)
        {
            game.NativeFsrSupported = true;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(fsrDll);
                game.NativeFsrVersion = !string.IsNullOrWhiteSpace(vi.ProductVersion) ? vi.ProductVersion : (vi.FileVersion ?? "FSR");
            }
            catch
            {
                game.NativeFsrVersion = "Detected";
            }
        }

        game.NativeFrameGenSupported = nearbyFiles.Any(f =>
        {
            string n = Path.GetFileName(f).ToLowerInvariant();
            return n is "nvngx_dlssg.dll" or "sl.dlss_g.dll" or "amd_fidelityfx_framegeneration_dx12.dll" or "libxess_fg.dll";
        });
    }

    private static void CollectNearbyDlls(string dir, int depth, int maxDepth, List<string> output)
    {
        if (depth > maxDepth) return;
        try
        {
            foreach (string dll in Directory.GetFiles(dir, "*.dll"))
                output.Add(dll);

            foreach (string sub in Directory.GetDirectories(dir))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                CollectNearbyDlls(sub, depth + 1, maxDepth, output);
            }
        }
        catch { }
    }

    private static string DetectEngine(string installRoot, string exeDir, string exePath)
    {
        string exeName = Path.GetFileName(exePath);
        if (exeName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(Path.Combine(installRoot, "Engine")))
            return "Unreal Engine";

        if (File.Exists(Path.Combine(exeDir, "UnityPlayer.dll")) ||
            Directory.GetDirectories(installRoot, "*_Data").Length > 0)
            return "Unity";

        if (Regex.IsMatch(exeName, @"^re\d+\.exe$", RegexOptions.IgnoreCase) ||
            File.Exists(Path.Combine(exeDir, "re_chunk_000.pak")))
            return "RE Engine";

        if (Directory.Exists(Path.Combine(installRoot, "r6")) &&
            Directory.Exists(Path.Combine(installRoot, "archive")))
            return "REDengine 4";

        if (File.Exists(Path.Combine(exeDir, "RDR2.exe")) ||
            File.Exists(Path.Combine(exeDir, "GTA5_Enhanced.exe")))
            return "RAGE Engine";

        return "Custom / Native";
    }
}
