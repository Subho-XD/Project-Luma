# Project Luma v3.1.0 — Performance Benchmarks

**Measured Date:** 2026-11-20  
**Target Stack:** `.NET 10` (`net10.0-windows`, `.NET 10.0.15`) + Native WPF Hardware-Accelerated Desktop UI (`x64`, Release)  
**Test Environment:** Windows 11 Pro (`10.0.26300`), AMD Ryzen 5 5500 (`6C/12T`), `31.9 GB` RAM, AMD Radeon RX 6600 (`VEN_1002&DEV_73FF`, RDNA 2 / `gfx1032`, `7.96 GB` VRAM, Adrenalin `26.11.2` / WDDM `32.0.21045.29003`)

---

## 1. CLI Health-Check & Standalone Installer Lifecycle Latency

Measured directly against both the standalone single-file installer (`release/v3.1.0/ProjectLuma-Setup-x64.exe`, `216,314,693` bytes, `471` embedded payload files) in an isolated temp directory (with zero adjacent `payload/` directory) and the compiled Release binaries:

| Operation / Mode | Context | Measured Time |
|---|---|---|
| `ProjectLuma-Setup-x64.exe --verify-package` | Embedded `payload.zip` integrity (`471` files) | **2153.7 ms** |
| `ProjectLuma-Setup-x64.exe --silent --no-shortcuts --install-dir <temp>` | Cold standalone extraction + registry + uninstaller deploy | **5055.3 ms** |
| `ProjectLuma.exe --health-check` (Self-contained `.exe`, Cold) | Installed standalone runtime probe | **1047.7 ms** |
| `ProjectLuma.exe --health-check` (Self-contained `.exe`, Warm) | Installed standalone runtime probe | **1007.0 ms** |
| `ProjectLuma.dll --health-check` (Framework-dependent `.dll`, Cold / Warm) | Direct `.NET 10` shared-runtime baseline | **260.1 ms** / **211.3–227.8 ms** |
| `ProjectLuma-Setup-x64.exe --repair --install-dir <temp>` | Standalone repair + updater verification | **7049.4 ms** |
| `ProjectLuma-Uninstall.exe --uninstall --install-dir <temp>` | Clean directory, Start Menu & registry removal | **2057.1 ms** |
| `ProjectLuma.Updater.exe --verify-self-test` | Atomic swap + failed health-check rollback test | **1050.4 ms** |

---

## 2. Game Library Scaling Benchmark (`6 / 100 / 500 / 1,000` Games)

Measured directly against compiled `.NET 10` Release assemblies (`ProjectLuma.Core.dll`, `ProjectLuma.Compatibility.dll`, `ProjectLuma.UI.dll`) across 5 iterations (median after JIT warmup), mirroring `CapabilityEngine.EvaluateGame`, `CapabilityEngine.PopulateFriendlyStatuses`, `GameCardViewModel`, search/status filter, sort, and 3-column `GameCoverRow` virtual slicing:

| Library Size | Virtual Rows (`3` cols) | Eval + Card Creation | Filter + Sort + Chunk | Total Pipeline Time |
|---|---|---|---|---|
| **6 games** (Typical local library) | `2` rows | **0.04 ms** | **0.00 ms** | **0.04 ms** |
| **100 games** | `34` rows | **0.37 ms** | **0.06 ms** | **0.43 ms** |
| **500 games** | `167` rows | **1.35 ms** | **0.24 ms** | **1.59 ms** |
| **1,000 games** | `334` rows | **1.75 ms** | **0.51 ms** | **2.25 ms** |

---

## 3. WPF Virtualized Shell & Rendering Latency

Measured from the WPF virtualized shell harness (`benchmarks/wpf_result.json`) and xUnit Release suite (`tests/ProjectLuma.Tests`):

| Benchmark | Measured Result | Target Budget |
|---|---|---|
| WPF Virtualized Cold Window Startup | **468.94 ms** | < 1,000 ms |
| Idle Process Working Set | **131.44 MB** | < 200 MB |
| 1,000-Game Loaded Working Set | **141.01 MB** | < 250 MB |
| Virtualized 1,000-Card Layout Pass | **33.94 ms** | < 100 ms |
| 1,000-Game Search Query Latency | **1.28 ms** | < 16 ms |
| 200 px Artwork Decode Pipeline | **26.06 ms** | < 50 ms |
| `ThousandGameLibrary_EvaluatesFiltersSortsAndChunksUnderThreshold` | **8 ms** | < 500 ms |
| Live DXGI + Registry + HIP Hardware Probe (`LiveHardwareDetection_Finds_AMD_RX6600_With_64Bit_Vram`) | **887 ms** (cold suite probe) | < 1,500 ms |

---

## 4. Download, Watcher & Archive Extraction Responsiveness

- **Non-Blocking Streaming Downloads (`GitHubPackageManager.StreamWithResumeAndRetryAsync`):** Streams GitHub releases asynchronously in `80 KB` buffers with `15m` archive timeout (`20s` metadata probes), isolated `Accept` headers (`application/octet-stream` preserved across redirects), HTTP `416` `.part` reset + retry, 3-attempt exponential backoff, disk-verified manifest state, and `.part` cancellation cleanup (`PackageManager_DownloadAndStageDetailed_ReportsProgress_And_CleansUpPartialOnCancel`: **79 ms**).
- **UI-Responsive Archive Extraction & Hashing (`ExtractZipSafely`, `ExtractSevenZipSafelySinglePass`, `WritePackageMetadataAsync`):** Offloads extraction (`91%–98%` throttled progress every `100 ms`) and SHA-256 verification to background `Task.Run(...)` threads while keeping the WPF Dispatcher responsive (`UiAutomation_ClickDownloadOfficial_RemainsResponsive_UpdatesProgress_SupportsCancel_And_HandlesInvalidArchive`: **2 s**).
- **Scoped Game Library & Downloads Discovery:** Bounded game-folder walk (`depth <= 3`, **54 ms** in `UserDllVault_ScopedGameFolderDiscovery_RespectsDepth3_And_CcModelThreshold`) paired with a debounced `%USERPROFILE%\Downloads` `FileSystemWatcher` (**635 ms** end-to-end automated DLL + archive discovery test).

---

## 5. Automated Test Execution Performance

- **Full Solution xUnit Run (`ProjectLuma.sln`, Release):** **30 / 30 tests passed in 6.6910 seconds** (`0` warnings, `0` errors).
