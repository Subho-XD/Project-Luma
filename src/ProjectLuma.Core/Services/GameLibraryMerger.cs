using ProjectLuma.Core.Enums;
using ProjectLuma.Core.Models;

namespace ProjectLuma.Core.Services;

/// <summary>
/// Shared game-library merge, deduplication, and path-availability evaluator used across
/// startup loading, full library scans, and manual game additions.
/// </summary>
public static class GameLibraryMerger
{
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string trimmed = path.Trim();
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    public static bool AreSameGame(DiscoveredGame? a, DiscoveredGame? b)
    {
        if (a == null || b == null) return false;
        if (!string.IsNullOrWhiteSpace(a.Id) && string.Equals(a.Id, b.Id, StringComparison.OrdinalIgnoreCase))
            return true;

        string exeA = NormalizePath(a.ExecutablePath);
        string exeB = NormalizePath(b.ExecutablePath);
        if (!string.IsNullOrWhiteSpace(exeA) && !string.IsNullOrWhiteSpace(exeB) &&
            string.Equals(exeA, exeB, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string dirA = NormalizePath(a.InstallDirectory);
        string dirB = NormalizePath(b.InstallDirectory);
        bool sameInstallDir = !string.IsNullOrWhiteSpace(dirA) &&
                              !string.IsNullOrWhiteSpace(dirB) &&
                              string.Equals(dirA, dirB, StringComparison.OrdinalIgnoreCase);

        if (sameInstallDir)
        {
            string exeNameA = Path.GetFileName(a.ExecutablePath);
            string exeNameB = Path.GetFileName(b.ExecutablePath);
            if (!string.IsNullOrWhiteSpace(exeNameA) && !string.IsNullOrWhiteSpace(exeNameB) &&
                string.Equals(exeNameA, exeNameB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (a.Launcher == GameLauncherType.Manual || b.Launcher == GameLauncherType.Manual)
                return true;
        }

        if (a.Launcher != GameLauncherType.Manual &&
            a.Launcher == b.Launcher &&
            !string.IsNullOrWhiteSpace(a.StoreAppId) &&
            string.Equals(a.StoreAppId, b.StoreAppId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static void EvaluateAvailability(
        DiscoveredGame game,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? rootExists = null)
    {
        Func<string, bool> checkFile = fileExists ?? File.Exists;
        Func<string, bool> checkRoot = rootExists ?? Directory.Exists;

        if (!string.IsNullOrWhiteSpace(game.ExecutablePath) && checkFile(game.ExecutablePath))
        {
            game.IsAvailable = true;
            game.AvailabilityReason = string.Empty;
            return;
        }

        game.IsAvailable = false;
        string? root = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
                root = Path.GetPathRoot(game.ExecutablePath);
            else if (!string.IsNullOrWhiteSpace(game.InstallDirectory))
                root = Path.GetPathRoot(game.InstallDirectory);
        }
        catch
        {
            root = null;
        }

        if (!string.IsNullOrWhiteSpace(root) && !checkRoot(root))
        {
            game.AvailabilityReason = $"Drive {root.TrimEnd('\\')} is currently disconnected.";
        }
        else if (!string.IsNullOrWhiteSpace(game.ExecutablePath))
        {
            game.AvailabilityReason = $"Game executable not found at {game.ExecutablePath} (drive or folder unavailable).";
        }
        else
        {
            game.AvailabilityReason = "Game installation path is currently unavailable.";
        }
    }

    public static List<DiscoveredGame> MergeScannedWithPersisted(
        IReadOnlyList<DiscoveredGame> scannedGames,
        IReadOnlyList<DiscoveredGame> persistedGames,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? rootExists = null)
    {
        var dedupedScanned = new List<DiscoveredGame>(scannedGames.Count);
        foreach (var scanned in scannedGames)
        {
            EvaluateAvailability(scanned, fileExists, rootExists);
            int idx = dedupedScanned.FindIndex(existing => AreSameGame(existing, scanned));
            if (idx < 0)
            {
                dedupedScanned.Add(scanned);
            }
            else if (dedupedScanned[idx].Launcher == GameLauncherType.Manual && scanned.Launcher != GameLauncherType.Manual)
            {
                // Prefer authoritative store identity while preserving manual flags if any
                scanned.IsManuallyAdded = scanned.IsManuallyAdded || dedupedScanned[idx].IsManuallyAdded;
                if (string.IsNullOrWhiteSpace(scanned.CustomInstallLocation))
                    scanned.CustomInstallLocation = dedupedScanned[idx].CustomInstallLocation;
                dedupedScanned[idx] = scanned;
            }
        }

        var merged = new List<DiscoveredGame>(dedupedScanned);

        foreach (var persisted in persistedGames)
        {
            int matchIdx = merged.FindIndex(m => AreSameGame(m, persisted));
            if (matchIdx >= 0)
            {
                var current = merged[matchIdx];
                // Keep persisted Id so GameProfiles and artwork_cache/<id>/custom.json remain linked across restarts
                if (!string.IsNullOrWhiteSpace(persisted.Id))
                    current.Id = persisted.Id;

                current.IsManuallyAdded = current.IsManuallyAdded || persisted.IsManuallyAdded;
                if (!string.IsNullOrWhiteSpace(persisted.CustomInstallLocation))
                    current.CustomInstallLocation = persisted.CustomInstallLocation;
                else if (current.IsManuallyAdded && string.IsNullOrWhiteSpace(current.CustomInstallLocation))
                    current.CustomInstallLocation = current.InstallDirectory;

                if (persisted.AddedAtUtc.HasValue)
                    current.AddedAtUtc = persisted.AddedAtUtc;

                if (string.IsNullOrWhiteSpace(current.InstalledBackendId) && !string.IsNullOrWhiteSpace(persisted.InstalledBackendId))
                {
                    current.InstalledBackendId = persisted.InstalledBackendId;
                    current.InstalledBackendVersion = persisted.InstalledBackendVersion;
                    current.InstalledFeatures = persisted.InstalledFeatures.ToList();
                    current.LastEnhancedUtc = persisted.LastEnhancedUtc;
                }

                PreserveCustomOrValidArtwork(current, persisted, fileExists);
                EvaluateAvailability(current, fileExists, rootExists);
                merged[matchIdx] = current;
            }
            else
            {
                EvaluateAvailability(persisted, fileExists, rootExists);
                merged.Add(persisted);
            }
        }

        return merged
            .OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void PreserveCustomOrValidArtwork(
        DiscoveredGame target,
        DiscoveredGame source,
        Func<string, bool>? fileExists)
    {
        Func<string, bool> checkFile = fileExists ?? File.Exists;
        bool sourceHasCustom = source.HasCustomArtwork || source.ArtworkSource == ArtworkSourceKind.CustomUser;
        if (sourceHasCustom)
        {
            target.HasCustomArtwork = true;
            if (!string.IsNullOrWhiteSpace(source.CoverImagePath) && checkFile(source.CoverImagePath))
                target.CoverImagePath = source.CoverImagePath;
            if (!string.IsNullOrWhiteSpace(source.HeroImagePath) && checkFile(source.HeroImagePath))
                target.HeroImagePath = source.HeroImagePath;
            if (!string.IsNullOrWhiteSpace(source.LogoImagePath) && checkFile(source.LogoImagePath))
                target.LogoImagePath = source.LogoImagePath;
            if (!string.IsNullOrWhiteSpace(source.IconImagePath) && checkFile(source.IconImagePath))
                target.IconImagePath = source.IconImagePath;
            target.ArtworkSource = ArtworkSourceKind.CustomUser;
            return;
        }

        if (string.IsNullOrWhiteSpace(target.CoverImagePath) && !string.IsNullOrWhiteSpace(source.CoverImagePath) && checkFile(source.CoverImagePath))
            target.CoverImagePath = source.CoverImagePath;
        if (string.IsNullOrWhiteSpace(target.HeroImagePath) && !string.IsNullOrWhiteSpace(source.HeroImagePath) && checkFile(source.HeroImagePath))
            target.HeroImagePath = source.HeroImagePath;
        if (string.IsNullOrWhiteSpace(target.LogoImagePath) && !string.IsNullOrWhiteSpace(source.LogoImagePath) && checkFile(source.LogoImagePath))
            target.LogoImagePath = source.LogoImagePath;
        if (target.ArtworkSource == ArtworkSourceKind.None && source.ArtworkSource != ArtworkSourceKind.None)
            target.ArtworkSource = source.ArtworkSource;
    }
}
