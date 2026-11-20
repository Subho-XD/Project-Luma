# Project Luma — Existing Project Audit (Pre-Redesign Baseline v1.0.0)

**Audit Date:** 2026-11-19  
**Pre-Redesign Backup Snapshot:** `backups/ProjectLuma-PreRedesign-v1.0.0.zip` (`4,349,315` bytes)

---

## 1. Current Technology Stack & Solution Layout

Inspected directly from `ProjectLuma.sln` and `src/*/*.csproj`:
- **Language & Runtime:** C# 13 / `.NET 10` (`SDK 10.0.404`, runtime `10.0.15`, `net10.0` + `net10.0-windows`).
- **UI Framework:** Windows Presentation Foundation (`UseWPF=true`, `ProjectLuma.UI` + `ProjectLuma.App`), MVVM pattern with `Microsoft.Extensions.Hosting` DI container.
- **Database & Storage:** `Microsoft.Data.Sqlite` (`11.0.0`, `%LocalAppData%\ProjectLuma\luma.db`) + `System.Text.Json`.
- **Hardware Interop:** Direct DXGI COM vtable interop (`dxgi.dll` `CreateDXGIFactory1` / `EnumAdapters1` / `GetDesc1`), Windows Display Class Registry, `System.Management` (`WMI`), `Avx2.IsSupported`, and HIP runtime DLL probing (`amdhip64_7.dll` / `amdhip64_6.dll`).
- **Archive & Packaging:** `SharpCompress` (`0.50.10`) for `.7z` and `System.IO.Compression` for `.zip`; self-contained `win-x64` publish size `144.78 MB` (`dist/ProjectLuma`).
- **Projects in `ProjectLuma.sln` (14 total):**
  1. `ProjectLuma.Core` — Domain models, enums, interfaces.
  2. `ProjectLuma.Logging` — Thread-safe JSONL + plain-text ring-buffered logger.
  3. `ProjectLuma.GpuDetection` — DXGI + Registry + WMI + PCI catalog detector.
  4. `ProjectLuma.Compatibility` — 7-state `CapabilityEngine` + SQLite repository.
  5. `ProjectLuma.GameDiscovery` — 7-store scanner + `VdfParser` + `PeAnalyzer` + `AntiCheatDetector`.
  6. `ProjectLuma.PackageManager` — Allowlisted GitHub Release client + `UserDllVault`.
  7. `ProjectLuma.BackendManager` — Independent adapters (`DLSS-NR-on-AMD`, `OptiScaler`, `DLSS-RR`) + orchestrator.
  8. `ProjectLuma.GameInstaller` — `IniMerger` + `TransactionalGameInstaller` (process guard, backup manifest, SHA-256 verification, drift detection, rollback).
  9. `ProjectLuma.UpdateService` — Background `IHostedService` update coordinator.
  10. `ProjectLuma.UI` — Design tokens, vector icons, ViewModels, 6 views.
  11. `ProjectLuma.App` — Entry point, DI wiring, diagnostic & screenshot harness.
  12. `ProjectLuma.Updater` — Standalone atomic self-updater with `--health-check` rollback.
  13. `ProjectLuma.Setup` — Basic 1-screen Windows installer (`ProjectLuma.Setup.exe`).
  14. `tests/ProjectLuma.Tests` — 12 automated xUnit tests (12/12 passing).

---

## 2. Component Retention & Redesign Classification

| Component | Current State & Assessment | Disposition |
|---|---|---|
| `ProjectLuma.Core` | Clean domain contracts, but lacks friendly user statuses, simple profile presets, artwork paths, and setup-progress step models. | **Retain & Extend** |
| `ProjectLuma.Logging` | Reliable JSONL + text logging under `%LocalAppData%\ProjectLuma\logs`. | **Retain** |
| `ProjectLuma.GpuDetection` | Accurate DXGI 64-bit VRAM + PCI lookup (`1002:73FF` RX 6600) + Adrenalin `26.11.2` detection; needs multi-GPU hybrid preference testing & simulated hardware fixtures. | **Retain & Harden** |
| `ProjectLuma.GameDiscovery` | Reliable 7-launcher scanner + PE32+ import analyzer + anti-cheat detector; lacks game artwork/metadata discovery and corrupt-manifest edge tests. | **Retain & Add `ArtworkDiscoveryService`** |
| `ProjectLuma.Compatibility` | Accurate 7-state evaluation; needs translation to plain-English gamer labels & 4 simple profile presets (`Visual Quality`, `Balanced`, `Smoother Gameplay`, `Custom`). | **Retain & Extend** |
| `ProjectLuma.PackageManager` | Safe allowlisted GitHub API + SHA-256 + Authenticode checks; exposes "User-Owned NVIDIA DLL Vault" directly in primary UX instead of an automatic resolver. | **Retain Core + Add `IntelligentComponentResolver`** |
| `ProjectLuma.BackendManager` | Solid adapter composition; needs default preset mapping so users never have to choose INI keys manually. | **Retain & Extend** |
| `ProjectLuma.GameInstaller` | Transactional backup, running-process guard, drift check, byte-exact rollback all verified passing; must remain intact. | **Retain & Add Edge-Case Tests** |
| `ProjectLuma.UpdateService` & `Updater` | Working background loop + verified atomic self-updater; keep intact. | **Retain** |
| `ProjectLuma.UI` (`OverviewView`, `GameLibraryView`, `GameDetailsView`, `EnhancementCentreView`, `UpdateCentreView`, `SettingsView`) | **Core Usability Defect:** Looks like a hardware developer dashboard rather than a PC gaming library; surfaces `NR/SR/FG/RR` tags on cards, opens on hardware stats instead of `My Games`, lacks real cover/hero/logo artwork, exposes raw DLL Vault & repo licenses upfront, lacks auto-dismissing toasts & first-run onboarding, and lacks `VirtualizingWrapPanel` optimization for 1,000 games. | **Complete UI/UX Redesign** |
| `ProjectLuma.Setup` | Single-screen basic installer (`SetupWindow.xaml`); lacks multi-screen compatibility report, component breakdown, disk check, and clean branding (`ProjectLuma-Setup-x64.exe`). | **Redesign into 5-Screen Branded Installer** |

---

## 3. Specific Bugs & UX Gaps Identified in Audit

1. **Developer-First Navigation & Home Screen:** Opens on `System Capability Overview` instead of `My Games`. Navigation has 6 engineering tabs instead of the clean 4-destination gaming flow (`My Games`, `Available Enhancements`, `Downloads & Updates`, `Settings`).
2. **Missing Visual Game Artwork:** Game cards show text monograms (`A`, `C2`, `GT`) instead of real portrait covers (`600x900`), wide hero banners (`1920x620`), and transparent logos already present on disk (e.g., Steam `appcache\librarycache\4126040`) or generated cleanly when absent.
3. **Technical Overload on Cards & Game Details:** Cards display `NR`, `SR`, `FG`, `RR` badges and `.exe` names directly; `GameDetailsView` immediately confronts gamers with `DLSS-NR-on-AMD`, `OptiScaler`, `FSR4-i8-2`, `RCAS`, `Fakenvapi`, and `Proxy DLL Target` without progressive disclosure (`Advanced Settings`).
4. **Confusing Primary "User-Owned NVIDIA DLL Vault":** Prominently invites ordinary gamers to hunt down and import NVIDIA DLLs even before trying `Sharper Image` (FSR 4.1.2 INT8) or `Smoother Gameplay`, which work out of the box on RX 6600.
5. **Notification & Long-Operation Lifecycle:** Status banners persist indefinitely until manually clicked (`Dismiss`), and there is no step-by-step setup drawer or first-run onboarding guide.
6. **Virtualization at 1,000+ Games:** Current `GameLibraryView` uses standard `WrapPanel` inside `ItemsControl`, which eagerly instantiates UI containers for all items instead of virtualizing rows/tiles cleanly at 1,000 games.
7. **Installer Branding & System Inspection:** Current `ProjectLuma.Setup.exe` does not inspect target GPU/driver/runtime/disk requirements or display a multi-step system readiness summary before installing.
