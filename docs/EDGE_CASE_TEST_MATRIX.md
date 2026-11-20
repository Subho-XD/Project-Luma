# Project Luma v3.1.0 — Edge-Case & Reliability Test Matrix

**Total Automated Tests:** `30` xUnit tests across `4` test classes (`ProjectLuma.Tests`)  
**Latest Release Execution Result:** **30 / 30 Passed (`0` Failed, `0` Skipped, `6.6910 s`, `0` Build Warnings/Errors)**

---

## 1. Test Coverage Matrix (`tests/ProjectLuma.Tests`)

| Category | Automated Verification | Edge Case Verified |
|---|---|---|
| **Game Discovery & PE Analysis** (`GameDiscoveryAndPeTests`) | `VdfParser_Parses_LibraryFolders_And_Rejects_PathTraversal_In_Acf` | Valve `.vdf`/`.acf` parsing and directory traversal rejection (`721 ms`) |
| **Game Discovery & PE Analysis** | `AntiCheatDetector_Identifies_EAC_BattlEye_And_WindowsApps` | EAC, BattlEye, and protected WindowsApps detection (`7 ms`) |
| **Game Discovery & PE Analysis** | `PeAnalyzer_Inspects_Real_64Bit_Windows_Binary` | Real 64-bit Windows PE inspection (`1 ms`) |
| **Game Discovery & Robustness** | `GameScanner_ResilientToCorruptedManifestsAndMissingExes` | Malformed manifests and missing game executables (`47 ms`) |
| **Game Discovery & Paths** | `PeAndArtwork_HandlesUnicodeAndLongPathsSafely` | Unicode folders and long file paths (`52 ms`) |
| **Artwork & Security** | `ArtworkDiscovery_LocalFolderOverrideAndDpapiRoundTripAndCacheClear` | Local artwork overrides, Windows DPAPI token protection & cache clear (`28 ms`) |
| **Artwork & Steam Cache** | `ArtworkDiscovery_SteamLocalCache_ResolvesRealCacheIfPresent` | Local Steam library cache resolution without mandatory network dependency (`5 ms`) |
| **GPU & Capability Engine** (`GpuAndCapabilityTests`) | `LiveHardwareDetection_Finds_AMD_RX6600_With_64Bit_Vram` | Live RX 6600 DXGI 64-bit VRAM (`7.96 GB`), `gfx1032`, Adrenalin `26.11.2`, HIP probe (`887 ms`) |
| **GPU & Capability Engine** | `CapabilityEngine_Rx6600_Reports_Honest_Neural_And_Gates_RayReconstruction_And_DlssFg` | Honest experimental RDNA 2 Neural Rendering and gating of unsupported Ray Reconstruction / DLSS-FG (`5 ms`) |
| **GPU & Capability Engine** | `PciDatabase_Classifies_All_RDNA_Generations_Accurately` | RDNA 1 / RDNA 2 / RDNA 3 / RDNA 4 & future architecture taxonomy (`< 1 ms`) |
| **HIP 7.2 & Truthfulness** | `GpuTarget_And_CapabilityEngine_Rx6600_Gfx1032_ReportsTruthfulHip72Exclusion` | Explicit `Hip72OfficiallySupported = false` truthfulness for `gfx1032` (`1 ms`) |
| **Multi-GPU Selection** | `MultiGpu_SelectBestGpuAdapter_PrioritizesDiscreteAmd` | Discrete Radeon prioritization over integrated iGPU (`4 ms`) |
| **Outdated / Missing Deps** | `OutdatedDriverAndMissingHip_ReportsHonestDependencyStates` | Accurate capability fallback when driver or HIP runtime is missing (`< 1 ms`) |
| **Presets, Guards & Anti-Cheat** | `CapabilityEngine_PresetsAndDoubleFgGuardAndAntiCheatWork` | Preset resolution, double-FG conflict prevention, anti-cheat gating (`31 ms`) |
| **Download Official UI Automation** | `UiAutomation_ClickDownloadOfficial_RemainsResponsive_UpdatesProgress_SupportsCancel_And_HandlesInvalidArchive` | Live WPF STA `ButtonAutomationPeer` clicking **Download Official** for corrupt archive error handling, mid-stream UI responsiveness + `.part` cancel cleanup, and full extraction (`2 s`) |
| **Package Manager & Streaming** | `PackageManager_DownloadAndStageDetailed_ReportsProgress_And_CleansUpPartialOnCancel` | `StreamWithResumeAndRetryAsync` HTTP streaming progress + cancellation `.part` removal (`79 ms`) |
| **Package Manager & Rollback** | `PackageManager_Stages_Local_Zip_And_RollsBack_Previous_Version` | Version staging (`ArchiveExistingActive`) and rollback restoration (`916 ms`) |
| **Package Endpoint Allowlist** | `PackageManager_Blocks_Untrusted_Endpoints_And_Allows_Official_GitHub` | Blocks untrusted hosts; restricts to `github.com` / `release-assets.githubusercontent.com` (`< 1 ms`) |
| **Autonomous Downloads Watcher** | `DownloadsFolderWatcher_And_AutoDiscovery_ImportsValidDllAndArchiveAutomatically` | Debounced `FileSystemWatcher` auto-discovery & validation of user-owned `nvngx_dlssnr.dll` + official archive (`635 ms`) |
| **Bounded DLL & CC Discovery** | `UserDllVault_ScopedGameFolderDiscovery_RespectsDepth3_And_CcModelThreshold` | Game-folder bounded search (`<= 3`) and `CC >= 310.9.16.0` vs `HT >= 310.8.0.0` classification (`54 ms`) |
| **DLL Security Validation** | `UserDllVault_Rejects_Disallowed_Or_Invalid_Dll_And_Accepts_Valid_64Bit_Dll` | Rejects malformed/32-bit/unauthorized DLLs; verifies PE32+ x64 headers & SHA-256 (`71 ms`) |
| **Genuine Component Actions** | `UpdateCoordinator_GeneratesGenuineComponentActions_And_NeverShowsInstallForAppOrIncompatibleBackend` | Disk-verified state checks, no redundant Install for Project Luma or AMDLSS5 (`65 ms`) |
| **Offline Update Resilience** | `UpdateCoordinator_Handles_Network_Failure_Gracefully_In_Offline_Mode` | Graceful offline fallback without unhandled exceptions (`95 ms`) |
| **Resolver & Cancellation** | `ComponentResolver_IdentifiesMissingUserOwnedDependencyAndSupportsCancel` | 14-step dependency resolution, missing user-owned guidance, and `CancellationToken` (`290 ms`) |
| **Transactional Game Installer** | `TransactionalGameInstaller_BacksUp_DetectsDrift_PreservesUserMods_And_RestoresByteExact` | `.luma_backup` transactional install, SHA-256 drift detection, and byte-exact rollback (`178 ms`) |
| **INI Preservation** | `IniMerger_Preserves_Comments_And_Custom_User_Keys` | Comment-preserving INI/TOML key merges (`9 ms`) |
| **Selective Uninstall Restore** | `InstallerEngine_UninstallWithOptions_RestoresBackedUpGameFiles_And_PreservesUserModifiedFiles` | Restores game backups while preserving user custom files (`138 ms`) |
| **Setup Installer Lifecycle** | `InstallerEngine_InstallRepairCancelUninstallLifecycle` | Install, repair, cancel rollback, and full uninstall cleanup (`2 s`) |
| **Launch Experience** | `GameLaunchService_LaunchesSteamUriAndDirectExe_WithTruthfulEnhancementSummary` | Steam URI (`steam://rungameid/...`) and direct `.exe` launch + summary (`40 ms`) |
| **1,000-Game Library Scaling** | `ThousandGameLibrary_EvaluatesFiltersSortsAndChunksUnderThreshold` | 1,000-game capability evaluation, search, sort, and virtual row slicing (`8 ms`) |

---

## 2. Standalone Installer & Visual Matrix Verification (`v3.1.0`)

- **Standalone Embedded-Payload Lifecycle (`release/v3.1.0/ProjectLuma-Setup-x64.exe`):** `--verify-package` (`471` files, `2153.7 ms`), `--silent` install (`5055.3 ms`), self-contained `--health-check` (`1047.7 ms` / `1007.0 ms`), `--repair` (`7049.4 ms`), `--uninstall` (`2057.1 ms`), and `ProjectLuma.Updater.exe --verify-self-test` (`1050.4 ms`) — all executed in an isolated temp folder with no external `payload/` directory and all exited `0`.
- **Multi-Resolution & DPI Visual QA (`docs/screenshots_v3_1/`):** Verified `40` rendered screenshots (`35` main app views across `720p`, `768p`, `1080p` at `100%–200%` DPI, `1440p`, `2160p` + `5` installer/uninstaller views) in `docs/RIOT_GAME_HUB_VISUAL_AUDIT.md`.
