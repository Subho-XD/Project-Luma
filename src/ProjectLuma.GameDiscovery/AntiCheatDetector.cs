using ProjectLuma.Core.Enums;

namespace ProjectLuma.GameDiscovery;

/// <summary>Detects kernel/user anti-cheat drivers and protected WindowsApps locations.</summary>
public static class AntiCheatDetector
{
    private static readonly string[] AntiCheatFolderNames =
    {
        "easyanticheat", "easyanticheat_eos", "battleye", "gameguard",
        "xigncode", "xigncode3", "punkbuster", "nprotect", "eaanticheat", "vanguard"
    };

    public static (AntiCheatStatus Status, string Details) Evaluate(string exeDirectory, string installRootDirectory)
    {
        if (IsInWindowsApps(installRootDirectory) || IsInWindowsApps(exeDirectory))
            return (AntiCheatStatus.WindowsAppsProtected, "Game resides in protected WindowsApps folder.");

        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(exeDirectory)) roots.Add(Path.GetFullPath(exeDirectory));
        if (!string.IsNullOrWhiteSpace(installRootDirectory)) roots.Add(Path.GetFullPath(installRootDirectory));

        // Also walk intermediate directories between exeDirectory and installRootDirectory
        if (!string.IsNullOrWhiteSpace(exeDirectory) && !string.IsNullOrWhiteSpace(installRootDirectory))
        {
            string fullExe = Path.GetFullPath(exeDirectory).TrimEnd('\\', '/');
            string fullRoot = Path.GetFullPath(installRootDirectory).TrimEnd('\\', '/');
            if (fullExe.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Path.GetDirectoryName(fullExe);
                while (!string.IsNullOrEmpty(parent) && parent.Length >= fullRoot.Length)
                {
                    roots.Add(parent);
                    parent = Path.GetDirectoryName(parent);
                }
            }
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string? match = ScanDirectory(root, 0, visited);
            if (match != null)
                return (AntiCheatStatus.DetectedBlocked, match);
        }

        return (AntiCheatStatus.None, string.Empty);
    }

    private static bool IsInWindowsApps(string path)
        => (path ?? string.Empty)
            .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(seg => seg.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));

    private static string? ScanDirectory(string dir, int depth, HashSet<string> visited)
    {
        if (!visited.Add(dir)) return null;
        try
        {
            foreach (string file in Directory.GetFiles(dir))
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                if (IsAntiCheatFile(name))
                    return $"Anti-cheat component detected ({Path.GetFileName(file)})";
            }

            if (depth >= 2) return null;

            foreach (string sub in Directory.GetDirectories(dir))
            {
                string folder = Path.GetFileName(sub).ToLowerInvariant();
                if (AntiCheatFolderNames.Contains(folder) || HasAntiCheatKeyword(folder))
                    return $"Anti-cheat directory detected ({Path.GetFileName(sub)})";

                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                string? nested = ScanDirectory(sub, depth + 1, visited);
                if (nested != null) return nested;
            }
        }
        catch
        {
            // Ignore unreadable protected folders
        }

        return null;
    }

    private static bool HasAntiCheatKeyword(string lower)
        => lower.Contains("anticheat") || lower.Contains("anti-cheat") || lower.Contains("anti_cheat");

    private static bool IsAntiCheatFile(string lower)
    {
        if (HasAntiCheatKeyword(lower)) return true;
        if (lower is "pbsvc.exe" or "x3.xem" or "start_protected_game.exe" or "vgk.sys" or "vgc.exe") return true;
        if (lower.StartsWith("gamemon") && lower.EndsWith(".des")) return true;
        if (lower.StartsWith("mhyprot") && lower.EndsWith(".sys")) return true;
        if (lower.StartsWith("easyanticheat") && (lower.EndsWith(".dll") || lower.EndsWith(".sys") || lower.EndsWith(".exe"))) return true;
        if (lower.StartsWith("beservice") && lower.EndsWith(".exe")) return true;
        if (lower.StartsWith("beclient") && lower.EndsWith(".dll")) return true;
        return false;
    }
}
