# Live In-App Update Test Report (`v3.3.1` -> `v3.3.2`)

## Summary
- **Result:** **PASSED** (Full end-to-end live upgrade executed from the existing installed `v3.3.1.0` baseline via Project Luma's own in-app self-updater against published GitHub Release `v3.3.2`)
- **Target Installation:** `<InstallDir>` (`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectLuma` `InstallLocation` = `F:\Games\ProjectLuma`, linked from Desktop shortcut)
- **GitHub Release Tested:** `https://github.com/Subho-XD/Project-Luma/releases/tag/v3.3.2`
- **Baseline Before Update:** `FileVersion = 3.3.1.0` (`HKCU Uninstall DisplayVersion = 3.3.1`)
- **Version After Update:** `FileVersion = 3.3.2.0` (`HKCU Uninstall DisplayVersion = 3.3.2`)

---

## Live E2E Execution Trace (`2026-11-21 UTC`)

Automated via WPF UI Automation driving the real installed `v3.3.1` application against the active `%LOCALAPPDATA%\ProjectLuma` data store:

1. **Baseline Launch (`09:13:02.96Z`):** Launched installed `F:\Games\ProjectLuma\ProjectLuma.exe` (`FileVersion = 3.3.1.0`, `PID 14956`) and attached to main window `"Project Luma — AMD Gaming Enhancement"`.
2. **Open Downloads & Updates (`09:13:08.96Z`):** Activated `NavDownloadsBtn`; confirmed `DownloadsAppVersionText == "v3.3.1"`.
3. **Check for Updates (`09:13:12.26Z`):** Invoked `DownloadsCheckUpdatesBtn`; confirmed live GitHub Release `v3.3.2` was detected (`UpdateAction_project-luma-app = "Update Project Luma"`, `Status = "Project Luma v3.3.2 is available and SHA-256 verified."`).
4. **In-App Download, Verify & Stage (`09:13:15.48Z` – `09:13:35.88Z`):** Triggered `UpdateAction_project-luma-app`. Downloaded `ProjectLuma-v3.3.2-win-x64.zip` (`110,733,619` bytes), verified SHA-256 (`79385562E2DD654B43487E935A84C106788C9D4511926CC54C384B1DCDAC0807`, matching `SHA256SUMS.txt`), staged files to `%LOCALAPPDATA%\ProjectLuma\self_update\staging\3.3.2_3831e4013`, spawned `ProjectLuma.Updater.exe` (`PID 24408`) from temp runner storage, and exited `v3.3.1` cleanly (`ExitCode = 0`).
5. **Transactional Swap, Health Check & Relaunch (`09:13:35.96Z` – `09:13:41.12Z`):**
   - `self-update.log` excerpt (sanitized paths):
     ```text
     [2026-11-21T09:13:35.9629978Z] Waiting for PID 14956 to exit...
     [2026-11-21T09:13:36.6188271Z] PID 14956 exited cleanly.
     [2026-11-21T09:13:36.6209513Z] Starting transactional update: stage='%LOCALAPPDATA%\ProjectLuma\self_update\staging\3.3.2_3831e4013', target='F:\Games\ProjectLuma'
     [2026-11-21T09:13:37.3249773Z] Running health check on 'F:\Games\ProjectLuma\ProjectLuma.exe'
     [2026-11-21T09:13:37.5968956Z] Health check exited 0.
     [2026-11-21T09:13:37.5999296Z] Updated HKCU Uninstall DisplayVersion to '3.3.2'.
     [2026-11-21T09:13:37.6663668Z] Transactional update committed successfully.
     [2026-11-21T09:13:37.6723305Z] Relaunched 'F:\Games\ProjectLuma\ProjectLuma.exe' (PID 25560).
     ```
   - Confirmed `.updater_rollback` directory was cleanly removed (`RollbackDirLeftBehind = False`).
6. **Post-Restart Version & Up-to-Date Verification (`09:13:41.12Z` – `09:13:55.12Z`):**
   - Confirmed `F:\Games\ProjectLuma\ProjectLuma.exe` `FileVersion = 3.3.2.0` and `HKCU Uninstall DisplayVersion = 3.3.2`.
   - Navigated to `Downloads & Updates` in relaunched GUI (`PID 25560`): confirmed `DownloadsAppVersionText == "v3.3.2"`.
   - Clicked `Check for Updates`: confirmed `DownloadsSyncStatusText = "Synchronized"`, `AppUpdateStateBadge = "Already up to date"`, `UpdateStatus_project-luma-app = "Already up to date (v3.3.2 matches the latest GitHub release)."`, and `UpdateAction_project-luma-app` hidden.
7. **Post-Update NeuralHub Verification (`09:13:59.48Z`):**
   - Navigated to `Enhancement Library`: confirmed external NeuralHub card remained detected (`Status = "Installed & Detected"`, `Version = "Version: v0.9.0"`).

---

## Post-Update User Data & Component Preservation Checks

| Verification Item | Pre-Update Baseline (`v3.3.1`) | Post-Update Result (`v3.3.2`) | Status |
| :--- | :--- | :--- | :--- |
| Installed Binary `FileVersion` | `3.3.1.0` | `3.3.2.0` | Verified |
| `HKCU` Uninstall `DisplayVersion` | `3.3.1` | `3.3.2` | Verified |
| Cached Update Archive SHA-256 | — | `79385562E2DD654B43487E935A84C106788C9D4511926CC54C384B1DCDAC0807` (`110,733,619` bytes) | Exact Match |
| Existing On-Disk Game Entries (`luma.db`) | 5 on-disk entries (`Aniimo`, `Hydra`, `Logo Grid Generator Pro 2.1`, `ProjectLuma`, `Riot Games`)* | All 5 on-disk entries intact | Preserved |
| Game Enhancement Profiles (`luma.db`) | 17 saved profiles (incl. `Custom` / `FSR Performance`) | All 17 saved profiles intact | Preserved |
| User Preferences (`AppSettings` JSON) | `CustomLibraryPaths`, `AutoCheckUpdates`, `AutoDownloadVerifiedUpdates`, `MinimizeToTray`, etc. | Identical user preferences retained | Preserved |
| User Imported DLL DB Rows | 3 rows (`nvngx_dlss.dll`, `nvngx_dlssg.dll`, `nvngx_dlssnr.dll`) | All 3 rows retained | Preserved |
| DLL Vault `nvngx_dlssnr.dll` (`310.9.16.0`) | `CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED` (`276,162,560` bytes) | `CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED` (`276,162,560` bytes) | Byte-Exact |
| External NeuralHub Store (`%LOCALAPPDATA%\Danielblnc\NeuralHub\games.json`) | `CD91B81A1FF6930A215098E9809889FCA732B848F10D624B124367CC5346B8C2` | `CD91B81A1FF6930A215098E9809889FCA732B848F10D624B124367CC5346B8C2` | Byte-Exact |

*\*Note on historical `Bright Memory - Infinite` entry: its game folder (`F:\Games\Bright Memory - Infinite`) was already absent on disk before the update test started, so `v3.3.1`'s initial startup scan (`09:13:04Z`) removed it prior to updating to `v3.3.2`; `v3.3.2` adds disconnected-path retention (`GameLibraryMerger` / `Unavailable` status) going forward, and preserved all existing database profiles including `manual:brightmemoryinfinite-win64-shipping:4553FC31`.*
