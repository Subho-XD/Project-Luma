# Project Luma v3.1.0 — Automated & End-to-End Test Results

**Verification Date:** 2026-11-20  
**Target Environment:** Windows 11 Pro Build `26300`, AMD Ryzen 5 5500, `31.9 GB` RAM, AMD Radeon RX 6600 (`gfx1032`, `7.96 GB` VRAM, Adrenalin `26.11.2`), `.NET 10.0.15` (`SDK 10.0.404`)

---

## 1. Automated Solution Test Suite (`dotnet test ProjectLuma.sln -c Release`)

**Result:** **Passed! Failed: 0, Passed: 30, Skipped: 0, Total: 30, Total time: 6.6910 Seconds (`0` Warnings, `0` Errors)**

| Suite Class | Tests Passed | Highlights |
|---|---|---|
| `GameDiscoveryAndPeTests` | `3 / 3` | Valve VDF/ACF traversal protection (`721 ms`), EAC/BattlEye/WindowsApps detection (`7 ms`), 64-bit PE verification (`1 ms`) |
| `GpuAndCapabilityTests` | `3 / 3` | Live RX 6600 `gfx1032` & `7.96 GB` probe (`887 ms`), RDNA 2 Neural/RR/FG gating (`5 ms`), RDNA 1–4 PCI taxonomy (`< 1 ms`) |
| `InstallerAndPackageTests` | `6 / 6` | Zip stage + rollback (`916 ms`), byte-exact game restore (`178 ms`), GitHub allowlist (`< 1 ms`), INI merge (`9 ms`), offline updates (`95 ms`), PE32+ DLL vault validation (`71 ms`) |
| `RedesignAndEdgeCaseTests` | `18 / 18` | Live WPF STA `Download Official` UI automation (`2 s`), debounced `Downloads` watcher (`635 ms`), genuine update actions (`65 ms`), bounded depth-3 scan & CC model threshold (`54 ms`), 1,000-game scaling (`8 ms`), full installer lifecycle (`2 s`), HIP 7.2 exclusion truthfulness (`1 ms`) |

---

## 2. Standalone Installer, Maintenance & Updater CLI Verification (`release/v3.1.0/`)

Tested directly on the standalone `release/v3.1.0/ProjectLuma-Setup-x64.exe` (`216,314,693` bytes, SHA-256 `51883fe19b9a45cd8b5eb1d53f5f136b381a239186275415f35f0700f11ef797`) copied into an isolated temp folder with **no adjacent `payload/` directory**:

1. `ProjectLuma-Setup-x64.exe --verify-package` -> Exit code `0` (`Verified 471 installer payload files`, `2153.7 ms`).
2. `ProjectLuma-Setup-x64.exe --silent --no-shortcuts --install-dir <temp>` -> Exit code `0` (`5055.3 ms`); verified `<temp>\ProjectLuma.exe`, `<temp>\ProjectLuma-Uninstall.exe`, and `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectLuma` (`DisplayName = Project Luma`, `DisplayVersion = 3.1.0`).
3. `<temp>\ProjectLuma.exe --health-check` -> Exit code `0` (`1047.7 ms` cold / `1007.0 ms` warm self-contained).
4. `ProjectLuma-Setup-x64.exe --repair --install-dir <temp>` -> Exit code `0` (`7049.4 ms`, verified updater presence).
5. `<temp>\ProjectLuma-Uninstall.exe --uninstall --install-dir <temp>` -> Exit code `0` (`2057.1 ms`); confirmed `<temp>`, Start Menu shortcuts, and `HKCU\...\Uninstall\ProjectLuma` completely removed.
6. `dist\ProjectLuma\ProjectLuma.Updater.exe --verify-self-test` -> Exit code `0` (`1050.4 ms`).

---

## 3. Visual QA Matrix (`docs/screenshots_v3_1/`)

- **Main Application (`35` captures across `MyGames`, `GameDetails`, `GameDetails_AdvancedExpanded`, `AvailableEnhancements`, `AvailableEnhancements_Expanded`, `DownloadsAndUpdates`, `Settings`, `SetupDrawer`, `Onboarding_Step1`, `Onboarding_Step3`, `InGameNotification`):** Captured across `1280x720`, `1366x768`, `1920x1080` (`100%`, `125%`, `150%`, `175%`, `200%` DPI), `2560x1440`, and `3840x2160` (`AppExit=0`). Visually inspected via `read_file` confirming the Riot-style `YOUR GAMES` rail, `296 px` Game Hub hero & `▶ Play` CTA, slim 7 px dark scrollbars, genuine `Downloads & Updates` actions, and clean DPI scaling with zero overlap or clipping.
- **3-Stage Installer & Maintenance Uninstaller (`5` captures in `docs/screenshots_v3_1/installer/`, `SetupExit=0`):** Verified `Installer_Stage1_Welcome.png`, `Installer_Stage2_SystemCheck.png`, `Installer_Stage3_InstallFinish.png`, `Uninstaller_Main.png` (`UNINSTALLER · v3.1.0`), and `Uninstaller_Complete.png`.
