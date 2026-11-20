# Project Luma — Autonomous Neural Rendering Setup & Compliance Guide

**Product:** Project Luma v3.1.0  
**Primary Validation Hardware:** AMD Radeon RX 6600 (RDNA 2 / `gfx1032`), Windows 11 x64  
**Primary Upstream Research Source:** [danielblnc/DLSS-NR-on-AMD](https://github.com/danielblnc/DLSS-NR-on-AMD/releases)

---

## 1. Upstream Release & Hardware Truthfulness (Checked 2026-11-20)

| Aspect | Official / Observed Reality | Project Luma Behavior |
|---|---|---|
| **DLSS-NR-on-AMD Upstream** | Alpha `v0.9.0` (`2026-11-18`) introduces a redesigned RDNA 2 Neural Rendering / Super Resolution / Ray Reconstruction path and adds a faster **CC neural-rendering model**. | Reports experimental RDNA 2 status distinctly from official AMD support. |
| **NVIDIA Model DLL Thresholds** | **HT model:** requires user-owned `nvngx_dlssnr.dll >= 310.8.0.0`. **CC model:** requires `nvngx_dlssnr.dll >= 310.9.16.0`. | `UserDllVault` validates PE32+ x64 headers, file version (`>= 310.8.0.0`), SHA-256, and Authenticode signer, and computes `SupportsCcModel` (`>= 310.9.16.0`). |
| **AMD HIP SDK 7.2 Truthfulness** | AMD's official Windows HIP SDK 7.2 documentation explicitly excludes RX 6600 / `gfx1032`, even while community `DLSS-NR-on-AMD` release notes reference a HIP runtime path for RDNA 2. | `WindowsGpuDetector` resolves `GpuTarget = "gfx1032"` and `Hip72OfficiallySupported = false`. Project Luma never falsely presents `gfx1032` as officially AMD-HIP-7.2 supported. |
| **License Boundary** | `DLSS-NR-on-AMD` forbids third-party bundling/redistribution (custom personal, non-commercial license); NVIDIA proprietary DLLs are strictly non-redistributable. | Project Luma never bundles, mirrors, repackages, or scrapes unofficial DLL sites. |

---

## 2. Automated 14-Step Resolution & In-App Watcher Workflow

When a user selects **AI Visuals** or clicks **Auto-Scan PC & Downloads**:
1. Detect AMD GPU model, architecture (`RDNA2`), and `gfx` target (`gfx1032`).
2. Check Windows system directories (`System32`, `%HIP_PATH%`) for HIP runtime presence (`amdhip64_7.dll` vs `amdhip64_6.dll`).
3. Scan registered game installations (Steam, Epic, GOG, Ubisoft, EA, Battle.net, Xbox, custom directories) bounded to depth `<= 3` for compatible local `nvngx_dlss*.dll` files.
4. Scan `%USERPROFILE%\Downloads` for valid user-owned `nvngx_dlssnr.dll` files or independently downloaded official `DLSS-NR-on-AMD` archives/folders.
5. Validate 64-bit PE headers, minimum version thresholds, SHA-256 checksums, and Authenticode status via `UserDllVault`.
6. Resolve eligible open-source dependencies (`OptiScaler` `v0.9.5` under `GPL-3.0`) directly from verified allowlisted GitHub Releases (`github.com` / `release-assets.githubusercontent.com`).
7. When user-owned components (`DLSS-NR-on-AMD` package or legitimate `nvngx_dlssnr.dll`) are still absent, show one plain-English in-app card with an **Open Official Source** button.
8. Start a debounced non-blocking **`FileSystemWatcher` on `%USERPROFILE%\Downloads`** so as soon as the user saves the file from the official page, Project Luma validates, stages, and updates game readiness automatically with zero manual folder browsing.
9. Prepare game profile INI/proxy configuration safely inside `<GameExeDir>\.luma_backup\<SessionId>\`, verify SHA-256 integrity, and activate enhancements with full 1-click rollback.
