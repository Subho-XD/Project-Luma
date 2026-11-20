# Project Luma — GitHub Baseline Release (`v3.3.0`)

**Repository:** https://github.com/Subho-XD/Project-Luma  
**Baseline Branch / Commit:** `main` @ `06a05f7eb29c6a92bae30cdadb7e59188c0985f1`  
**Annotated Tag:** `v3.3.0` (`cd7c1bd0cb8a13bb8d8e9f07ad5041f821c858c3`)  
**GitHub Release URL:** https://github.com/Subho-XD/Project-Luma/releases/tag/v3.3.0  
**Pre-Baseline Local Backup:** `backups/ProjectLuma-PreGitHubBaseline-v3.3.0.zip` (excluded from Git via `.gitignore`)

---

## 1. Uploaded Release Assets & SHA-256 Verification

| Asset | Size (bytes) | SHA-256 (Verified Local & GitHub API Digest) |
|---|---:|---|
| `ProjectLuma-Setup-x64.exe` | 259,001,627 | `0758e306defc09d30a358266e41e2b6807b18a1deea015aaf54b4108164675d7` |
| `ProjectLuma-v3.3.0-win-x64.zip` | 110,694,499 | `b018797429f548215ad5ae61c83b83029e74e12712e4df937f0486241258427f` |
| `SHA256SUMS.txt` | 353 | `b4d906159966fccf6ead699848d58c892c41c5d2898b799d585df944bff6e458` |
| `README.txt` | 5,168 | `f66a78dcd2c2c7b6a3de77c3a8f0f3100a8f7f7c27800f849a7dd84074ea68cc` |
| `RELEASE_NOTES.md` | 1,790 | `67d057e40ae2c49b823a794e52df0478eaedac4e7fcef217a67d5fea2d69fffa` |

Binary asset hygiene check: `0` restricted NVIDIA DLLs (`nvngx_*.dll`, `nr-*.dll`), `0` upstream NeuralHub binaries (`neuralhub*.exe`, `neuralhub_host.dll`, `dlssnr_on_amd*`), and `0` personal local backups or secrets in `dist/` or `release/v3.3.0/` or Git tracked tree.

---

## 2. Baseline Self-Updater Wiring Included in `v3.3.0`

So that `v3.3.0` serves as a realistic baseline capable of updating itself live once `v3.3.1` is published:
- `AppSelfUpdateManager` + `UpdateCoordinatorService` checks `https://api.github.com/repos/Subho-XD/Project-Luma/releases?per_page=15`, parses `SHA256SUMS.txt` / GitHub asset `sha256` digest, downloads portable `ProjectLuma-v*-win-x64.zip` strictly over allowlisted HTTPS hosts with redirect inspection and per-read stall timeouts, verifies SHA-256 + Zip-Slip safety, extracts cleanly, copies `ProjectLuma.Updater.exe` into `%TEMP%`, and triggers parent exit.
- `ProjectLuma.Updater.exe` (`--verify-self-test` passed) waits on parent PID, swaps staged files atomically with `.updater_rollback`, runs `--health-check`, updates HKCU Uninstall `DisplayVersion` when `InstallLocation` matches, and relaunches `ProjectLuma.exe`.
- `LumaPaths.AppDataDirectory` supports `PROJECT_LUMA_APPDATA` isolation for clean side-by-side live update testing without disturbing production user settings or backups.

---

## 3. Baseline Build, Test & Packaging Verification

| Area | Status | Evidence |
|---|---|---|
| Release Solution Build (`ProjectLuma.sln`) | **Tested successfully** | `0` warnings, `0` errors |
| Automated Test Suite (`ProjectLuma.Tests`) | **Tested successfully** | `43 / 43` passed (`0` failed, `0` skipped, Release config) |
| Standalone Self-Contained Updater Self-Test | **Tested successfully** | `ProjectLuma.Updater.exe --verify-self-test` exited `0` |
| Standalone Single-File Installer Integrity | **Tested successfully** | `ProjectLuma-Setup-x64.exe --verify-package` verified 546 payload files |
| Application Health Check & Version | **Tested successfully** | `ProjectLuma.exe` `FileVersion = 3.3.0.0`, `--health-check` exited `0` |
| Live Self-Update (`v3.3.0 -> next release`) | **Not tested yet** | Deferred until Stage B fixes and Stage C (`v3.3.1`) publication |

---

## 4. Known Baseline Defects (Deliberately Preserved in `v3.3.0` Stage A)

1. **External NeuralHub v0.9.0 Detection Defect:** `NeuralHubDetector` checks only `%LOCALAPPDATA%\Programs\NeuralHub`, `%LOCALAPPDATA%\NeuralHub`, `%APPDATA%\NeuralHub`, and `%ProgramFiles%\NeuralHub` for `NeuralHub*.exe`, missing the official `danielblnc/DLSS-NR-on-AMD` installation location (`HKCU\Software\Danielblnc\NeuralHub`, `%LOCALAPPDATA%\Danielblnc\NeuralHub`, lowercase `neuralhub.exe`, and `runtime\v0.9.0\`). As a result, v3.3.0 reports `Version: Not Installed` and `NeuralHub v0.9.0 handoff still required` even when NeuralHub v0.9.0 is installed.
2. **NeuralHub vs Project Luma DLL Vault Synchronization:** v3.3.0 does not inspect `%LOCALAPPDATA%\Danielblnc\NeuralHub\data\index.ini` (`[nr]`) to recognize or SHA-256-match NeuralHub's already-imported `nvngx_dlssnr.dll`.
3. **Per-Game NeuralHub Loader & OptiScaler Conflict Inspection:** v3.3.0 does not read `%LOCALAPPDATA%\Danielblnc\NeuralHub\games.json` or differentiate NeuralHub proxy stubs / `neuralhub.ini` / `dlssnr_on_amd.log` from OptiScaler proxy DLLs inside game folders.

These defects are tracked for **Stage B** and will be resolved in **`v3.3.1`**.
