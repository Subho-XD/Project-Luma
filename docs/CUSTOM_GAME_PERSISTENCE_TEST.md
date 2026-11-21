# Custom Game Persistence & Drive Availability Report

## Overview
Fixed the defect where games added individually via the **Add Game** flow appeared in Recent Games / My Games for the current session but disappeared after restarting Project Luma unless the user also registered the parent folder under **Settings -> Game Library -> Additional Game Folders**.

---

## Root Cause
1. `MainViewModel.ScanLibrariesAsync()` previously replaced `AllGames` directly with the output of `MultiStoreGameScanner.ScanAllAsync()` and called `SqliteCompatibilityRepository.SaveDiscoveredGamesAsync(found)`, which executes `DELETE FROM DiscoveredGames;` followed by re-inserting only the newly scanned storefront/folder entries. Any individually added executable outside `Settings.CustomLibraryPaths` was dropped on restart.
2. `GameDirectoryInspector.Inspect()` did not verify PE validity when invoked directly on an `.exe` file path.
3. Cached records loaded in `InitializeAsync()` did not re-evaluate executable/drive availability when an external drive was temporarily disconnected.

---

## Implemented Architecture

- **Shared SQLite Library Model (`src/ProjectLuma.Core/Models/GameModels.cs`, `LumaEnums.cs`):**
  - Added `IsManuallyAdded`, `CustomInstallLocation`, `IsAvailable`, `AvailabilityReason`, `AddedAtUtc`, and `HasCustomArtwork` to `DiscoveredGame`.
  - Added `FriendlyGameStatus.Unavailable` for disconnected/missing game paths.
- **Direct Upsert Persistence (`src/ProjectLuma.Core/Interfaces/IServices.cs`, `src/ProjectLuma.Compatibility/SqliteCompatibilityRepository.cs`):**
  - Added `UpsertDiscoveredGameAsync(DiscoveredGame, CancellationToken)` so individual additions and artwork edits persist immediately into `luma.db` (`DiscoveredGames` table).
- **Shared Deduplication, Merge & Availability Engine (`src/ProjectLuma.Core/Services/GameLibraryMerger.cs`):**
  - `NormalizePath(path)`: normalizes full paths safely across directory separator variations.
  - `AreSameGame(a, b)`: deduplicates across normalized `ExecutablePath`, `InstallDirectory` + executable filename, shared `StoreAppId` + non-manual launcher, or matching install directory when combining manual and storefront entries.
  - `EvaluateAvailability(game)`: checks executable presence and drive root reachability, distinguishing disconnected drives (`"Drive X: is currently disconnected."`) from missing executables without deleting user records.
  - `MergeScannedWithPersisted(scanned, persisted)`: preserves stable game IDs (keeping `GameProfiles` and `artwork_cache\<id>\custom.json` intact), retains manual flags/custom locations/installed backend state/custom artwork, and preserves disconnected/unscanned persisted records marked `Unavailable`.
- **Executable Validation (`src/ProjectLuma.GameDiscovery/GameDirectoryInspector.cs`):**
  - Rejects non-PE files or DLLs passed to single-file `.exe` inspection (`pe == null || pe.IsDll`).
- **UI & Availability Status (`CapabilityEngine.cs`, `LumaConverters.cs`, `GameCardViewModel.cs`, `MainViewModel.cs`):**
  - `MainViewModel.AddGameManuallyFromPathAsync()` validates the executable, resolves custom install location, checks `GameLibraryMerger.AreSameGame` to prevent duplicates, resolves artwork, and persists immediately to SQLite.
  - `ScanLibrariesAsync()` and `InitializeAsync()` merge persisted SQLite records instead of overwriting them and evaluate drive availability cleanly.
  - `PlayGameAsync()` surfaces a clear alert if a user tries to launch an `Unavailable` game on a disconnected drive.

---

## Automated Verification (`PatchV332Tests`)

Executed in Release configuration (`dotnet test ProjectLuma.sln -c Release`):
- Test: `ProjectLuma.Tests.PatchV332Tests.Task2_CustomGamePersistence_CoversRestart_Artwork_DriveDisconnect_Reconnect_And_Dedup` (**Passed**, 5 s)

Verified scenarios:
1. **Add custom game manually (without Settings folder):** Persists executable path, install directory, `IsManuallyAdded = true`, `CustomInstallLocation`, and artwork in `luma.db` while leaving `Settings.CustomLibraryPaths` empty.
2. **Restart #1:** Recreated `MainViewModel` + `SqliteCompatibilityRepository` and ran `InitializeAsync(autoScan: true)` — custom game remained present and `IsAvailable == true`.
3. **Custom artwork modification + Restart #2:** Applied custom cover and hero images via `SetCustomArtworkFromPathAsync`, restarted again — `ArtworkSourceKind.CustomUser`, `HasCustomArtwork == true`, and cached image files remained intact.
4. **Drive disconnect simulation:** Moved/removed the external mount directory and restarted — record count remained `2` (zero deletion), marked `IsAvailable == false` and `FriendlyGameStatus.Unavailable` (`"Unavailable"`).
5. **Drive reconnect simulation:** Restored the external mount directory and restarted — automatically recovered `IsAvailable == true` with `CustomUser` artwork intact.
6. **Duplicate prevention:** Attempted to manually add both an already-discovered Steam game and the already-persisted manual game, and then added the parent folder to `Settings.CustomLibraryPaths` and rescanned — game count remained `2` with zero duplicates.
7. **Invalid executable guard:** Passed a non-PE text file with an `.exe` extension to `AddGameManuallyFromPathAsync` — rejected cleanly (`null` returned, alert displayed, DB unmodified).
