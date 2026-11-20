# Project Luma — Iterative Development Log

## Loop 1: Milestone 0 — Research, License Audit & Architecture
- Audited `danielblnc/DLSS-NR-on-AMD` (`v0.9.0` 2026-11-18, `v0.8.0` 2026-10-17), `Wehaveall/AMDLSS5` (`v2.0.0` 2026-11-06), `OptiScaler/OptiScaler` (`v0.9.5` 2026-11-16), and official AMD documentation (FFX 2.4 / FSR 4.1.2 INT8 RDNA 2, FSR-FG 4.0.2, AFMF 2, HIP SDK 7.2).
- Created `TECHNICAL_RESEARCH.md`, `GPU_COMPATIBILITY_MATRIX.md`, `THIRD_PARTY_LICENSES.md`, `BACKEND_INTEGRATION_PLAN.md`, `DESIGN_SYSTEM.md`, `.interface-design/system.md`, and seed catalogs under `data/` & `backends/`.

## Loops 2–6: Core Solution, Hardware Detection, Game Discovery, Backends, Installer & Self-Updater
- Built modular 14-project `.NET 10` solution (`ProjectLuma.sln`): Core, Logging, GpuDetection (DXGI + Registry + WMI + PCI DB + HIP probe), Compatibility (7-state CapabilityEngine + SQLite), GameDiscovery (7 launchers + VDF + PE32+ + AntiCheatDetector), PackageManager (allowlisted GitHub client + SHA-256 + Authenticode + UserDllVault), BackendManager (4 independent adapters), GameInstaller (transactional backup + INI merge + drift detection + byte-exact rollback), UpdateService, Updater, UI, App, Setup, and Tests.

## Loop 7: Visual Review & Live RX 6600 Diagnostics
- Executed live hardware capture on `AMD Radeon RX 6600` (`VEN_1002&DEV_73FF`, `RDNA2`, `7.96 GB`, Adrenalin `26.11.2`, `amdhip64_6.dll`) and rendered 18 screenshots across `1920x1080` (100%, 125%, 150% DPI) and `1280x720`.
- **Root Causes Found & Fixed During Visual/Diagnostic Loop:**
  1. Added `JsonStringEnumConverter` to `GitHubPackageManager` and `SqliteCompatibilityRepository` after logs caught `backends.json` enum parsing failure.
  2. Prioritized `RadeonSoftwareVersion` (`26.11.2`) over `ReleaseVersion` (`25.10...`) in `WindowsGpuDetector`.
  3. Excluded `repair.exe` / diagnostic utilities and added title-matching bonus in `GameDirectoryInspector` so `Aniimo.exe` is chosen cleanly.
  4. Fixed vertical clipping in `LumaTextBoxStyle`, wrapped sidebar hardware scope text, populated default `FG Pipeline` ComboBox value, and rebound `SelectedGame` on rescan.

## Loop 8 & Milestone 9: Regression Tests, Packaging, Installer & Backup
- Ran all 12 xUnit tests in Release (`12/12 Passed` in 1.53s).
- Verified `ProjectLuma.Updater --verify-self-test` (atomic swap + rollback on failed health check).
- Published self-contained `win-x64` release (`dist/ProjectLuma/`) and Windows Installer (`dist/Installer/ProjectLuma.Setup.exe`).
