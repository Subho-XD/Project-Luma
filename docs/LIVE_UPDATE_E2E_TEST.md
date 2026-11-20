# Live Application Self-Update End-to-End Verification (`v3.3.0 -> v3.3.1`)

**Repository Under Test:** https://github.com/Subho-XD/Project-Luma  
**Baseline Release (`v3.3.0`):** https://github.com/Subho-XD/Project-Luma/releases/tag/v3.3.0 (`Commit 06a05f7`)  
**Target Release (`v3.3.1`):** https://github.com/Subho-XD/Project-Luma/releases/tag/v3.3.1 (`Commit 37d6acd`)  
**Test Method:** Live Windows UI Automation (`System.Windows.Automation`) driving the installed `v3.3.0.0` GUI against live GitHub Releases (`Subho-XD/Project-Luma`) in an isolated `%TEMP%\ProjectLumaLiveUpdateTest` sandbox (`PROJECT_LUMA_APPDATA=%TEMP%\ProjectLumaLiveUpdateTest\AppData`).

---

## 1. Pre-Update Seeded User State (`v3.3.0`)

Before launching `v3.3.0.0`:
- Installed `release\v3.3.0\ProjectLuma-Setup-x64.exe` into `%TEMP%\ProjectLumaLiveUpdateTest\App` (`--silent --install-dir ... --no-register --no-shortcuts`), verified `ProjectLuma.exe` `FileVersion = 3.3.0.0` and `ProjectLuma.Updater.exe` present.
- Seeded `%TEMP%\ProjectLumaLiveUpdateTest\AppData\luma.db` with:
  - `MinimizeToTray = true`, `HasCompletedOnboarding = true`
  - Discovered game `Aniimo` (`StoreAppId = 4126040`, `DirectX 12`, `x64`)
  - Saved `GameEnhancementProfile` for `Aniimo` with `SelectedPreset = Custom` and `SelectedUpscalingPreset = Performance`
- Seeded `%TEMP%\ProjectLumaLiveUpdateTest\AppData\UserComponents\NVIDIA\DLSS-NR\310.9.16.0\metadata.json` and `%TEMP%\ProjectLumaLiveUpdateTest\AppData\backups\test_backup_marker.txt` (`BACKUP_MARKER_INTACT_3_3_0`).

---

## 2. Observed Live Self-Update Execution Trace

```text
[2026-11-20T20:32:22.5832196Z] Initial installed exe: %TEMP%\ProjectLumaLiveUpdateTest\App\ProjectLuma.exe (FileVersion=3.3.0.0)
[2026-11-20T20:32:22.6770177Z] Launched v3.3.0 PID=23556 with isolated PROJECT_LUMA_APPDATA=%TEMP%\ProjectLumaLiveUpdateTest\AppData
[2026-11-20T20:32:25.3816772Z] Found v3.3.0 main window: 'Project Luma — AMD Gaming Enhancement'
[2026-11-20T20:32:27.9490177Z] Navigated to Downloads via SetFocus+VK_SPACE
[2026-11-20T20:32:28.5035299Z] DownloadsAppVersionText=v3.3.0
[2026-11-20T20:32:31.0239646Z] Clicked Check for Updates via InvokePattern
[2026-11-20T20:32:34.0460181Z] Detected live update card: Action='Update Project Luma', Status='Project Luma v3.3.1 is available and SHA-256 verified.'
[2026-11-20T20:32:36.0577651Z] Clicked Update Project Luma via InvokePattern; waiting for download, verify, exit of PID 23556, swap, and relaunch...
[2026-11-20T20:32:47.8814535Z] v3.3.0 PID 23556 exited cleanly (ExitCode=0). Waiting for relaunched v3.3.1...
[2026-11-20T20:32:53.8225632Z] Detected relaunched ProjectLuma.exe PID=3432
[2026-11-20T20:32:53.8249576Z] Post-update binary FileVersion=3.3.1.0
[2026-11-20T20:32:53.8259997Z] Found relaunched v3.3.1 main window: 'Project Luma — AMD Gaming Enhancement'
[2026-11-20T20:32:57.1926676Z] Post-update DownloadsAppVersionText=v3.3.1
[2026-11-20T20:33:06.3830789Z] Post-update SyncStatus='Idle', ProjectLumaStatus='Synchronized with latest GitHub release (v3.3.1).', ActionButtonVisible=False
[2026-11-20T20:33:10.3967862Z] Post-update NeuralHub card: Status='Installed & Detected', Version='Version: v0.9.0'
[2026-11-20T20:33:10.5066909Z] Closed relaunched test process cleanly.
[2026-11-20T20:33:10.5114928Z] RollbackDirLeftBehind=False
```

---

## 3. Out-of-Process Updater Log (`logs\self-update.log`)

```text
[2026-11-20T20:32:47.5930447Z] Waiting for PID 23556 to exit...
[2026-11-20T20:32:48.2321783Z] PID 23556 exited cleanly.
[2026-11-20T20:32:48.2355637Z] Starting transactional update: stage='%TEMP%\ProjectLumaLiveUpdateTest\AppData\self_update\staging\3.3.1_946ee7422', target='%TEMP%\ProjectLumaLiveUpdateTest\App'
[2026-11-20T20:32:49.1804111Z] Running health check on '%TEMP%\ProjectLumaLiveUpdateTest\App\ProjectLuma.exe'
[2026-11-20T20:32:50.0203692Z] Health check exited 0.
[2026-11-20T20:32:50.1467137Z] Transactional update committed successfully.
[2026-11-20T20:32:50.1531930Z] Relaunched '%TEMP%\ProjectLumaLiveUpdateTest\App\ProjectLuma.exe' (PID 3432).
```

---

## 4. Post-Update Data Preservation & Failure-Mode Verification

### A. Preserved User State After `v3.3.0 -> v3.3.1` Upgrade

| Checked Item | Expected | Observed Post-Update |
|---|---|---|
| `ProjectLuma.exe` `FileVersion` | `3.3.1.0` | `3.3.1.0` |
| `DownloadsAppVersionText` | `v3.3.1` | `v3.3.1` |
| Re-running `Check for Updates` on `v3.3.1` | `Synchronized with latest GitHub release (v3.3.1).` (no update button) | `Synchronized with latest GitHub release (v3.3.1).` (`ActionButtonVisible=False`) |
| `Enhancement Library` NeuralHub Card | `Installed & Detected` (`Version: v0.9.0`) | `Installed & Detected` (`Version: v0.9.0`) |
| `Settings.MinimizeToTray` | `True` (and did not block update exit!) | `True` |
| `Settings.HasCompletedOnboarding` | `True` | `True` |
| `Aniimo` `SelectedUpscalingPreset` | `Performance` (`Custom`) | `Performance` (`Custom`) |
| DLL Vault `metadata.json` | Preserved | `VaultMetaExists=True` |
| Central Backup Marker (`backups\test_backup_marker.txt`) | `BACKUP_MARKER_INTACT_3_3_0` | `BACKUP_MARKER_INTACT_3_3_0` |
| `.updater_rollback` Cleanup | Removed after commit | `RollbackDirLeftBehind=False` |

### B. Automated Self-Updater Failure & Security Mode Verification ([NeuralHubAndLiveUpdateTests.cs](file:///F:/AI/Project%20Luma/tests/ProjectLuma.Tests/NeuralHubAndLiveUpdateTests.cs))

| Scenario | Verified Outcome |
|---|---|
| Offline / Network Error | Reports `Offline` badge + `Retry Check` button without crashing or hanging |
| GitHub API Rate Limit (`HTTP 429` / `403`) | Reports `Rate limited` badge + `Retry Check` button |
| SHA-256 Checksum Mismatch | Rejects archive, deletes mismatched file, does not create stage directory |
| Corrupted Zip Archive | Catches `InvalidDataException`, deletes corrupted archive and stage folder |
| Zip-Slip Path Traversal (`../../evil.exe`) | Blocks extraction and deletes stage folder |
| Non-Allowlisted Download / Redirect Host | Rejects non-GitHub or non-HTTPS URLs before socket connection |
| User Cancellation Mid-Download | Cancels stream immediately and deletes `.partial` file |
| Failed `--health-check` After File Swap | `ProjectLuma.Updater.Program.ApplyUpdateTransactional` restores all original files from `.updater_rollback` and cleans up rollback directory |
