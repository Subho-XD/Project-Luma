# Project Luma — Technical Research & Upstream Audit

**Audit Date:** 2026-11-19  
**Baseline Development & Verification Target:** AMD Radeon RX 6600 (`PCI\VEN_1002&DEV_73FF`, RDNA 2 / Navi 23, 8 GB GDDR6), AMD Ryzen 5 5500 (AVX2), 32 GB DDR4, Windows 11 Pro (Build 26300), Adrenalin WDDM `32.0.21045.29003` (26.x series).

> [!IMPORTANT]
> Upstream README performance claims are treated strictly as upstream developer assertions, not independently verified benchmark data. Project Luma never fabricates FPS metrics or unsupported hardware capabilities.

---

## 1. Upstream Repository Audits

### 1.1 Primary Neural Rendering Backend — `danielblnc/DLSS-NR-on-AMD`
- **Repository:** `https://github.com/danielblnc/DLSS-NR-on-AMD`
- **Inspected Releases:**
  - **Latest Release:** `v0.9.0` (`Alpha 0.9.0`), published `2026-11-18T07:34:12Z`. Release asset: `NeuralHub_v0.9.0_setup.exe` (`30,925,039` bytes).
  - **Baseline Major Release:** `v0.8.0` (`Neural Rendering Supermod`), published `2026-10-17T18:50:41Z`.
  - **RDNA 2 Introduction:** `v0.6.0`, published `2026-10-03T03:17:03Z` (added RX 6000 support requiring AMD HIP SDK 7.2).
- **Architecture & Implementation Findings:**
  - **NeuralHub:** Centralized mod manager introduced in `v0.7.0` / `v0.8.0` that stores the ~150 MB model weights and proxy loaders centrally rather than duplicating heavy binaries into every game folder. Keeps per-game installations thin via proxy DLL forwarding and configuration files (`dlssnr_on_amd.ini` / `dlssnr_on_amd.log`).
  - **Runtime & Execution Path:** Ground-up reimplementation of DLSS neural networks for AMD GPUs using AMD HIP kernels and DirectX 12 compute/interop.
    - **RDNA 3 (RX 7000) & RDNA 4 (RX 9000):** Run in native DirectX 12 mode (`v0.7.0+` / `v0.9.0`), eliminating interop stalls. Support DLSS Neural Rendering (HT and CC models; Fast, Reference, and RX 7000 Superfast modes), DLSS Super Resolution 4.5 (presets K, J, M, L), DLSS Ray Reconstruction (Model F), and DLSS Frame Generation (up to x4 in `v0.8.0`, up to x6 MFG with Streamline 2.14+ and Preset C Lite in `v0.9.0`), plus bundled FSR 4.1.1/4.1.2 and FSR-FG 4.0.1/4.0.2.
    - **RDNA 2 (RX 6000 / RX 6600):**
      - In `v0.8.0`, **only Neural Rendering is available in experimental/testing status** on RDNA 2; DLSS-SR, DLSS-FG, and DLSS-RR are not fully available on that baseline.
      - In `v0.9.0`, RX 6000 receives a redesigned HIP path for NR (adding `Superfast` alongside `Fast`, while `Reference` mode is explicitly marked unavailable on RX 6000) and experimental HIP paths for SR/RR, while **`DLSS-FG is explicitly unavailable on RX 6000`** (`Known issues: DLSS-FG is not available on RX 6000`).
  - **Required User-Provided Files (Never Bundled):**
    - `nvngx_dlssnr.dll`: Build `310.8.0.0` or later (HT model), `310.9.16.0` or later (CC model in `v0.9.0`).
    - Optional (RDNA 3/4, or experimental v0.9.0 testing): `nvngx_dlss.dll` (SR), `nvngx_dlssg.dll` (FG), `nvngx_dlssd.dll` (RR).
  - **Driver & OS Requirements:**
    - Windows 11, DirectX 12 game supporting DLSS, FSR, or XeSS inputs.
    - AMD Adrenalin driver `26.1.1` or later.
    - **RX 6000 (RDNA 2) Critical Prerequisite:** Requires **AMD HIP SDK 7.2 (`amdhip64_7.dll`)**. On our local verification system, `C:\Windows\System32\amdhip64_6.dll` (`10.0.3652.0`) is installed, whereas `amdhip64_7.dll` is not yet present — Project Luma's capability engine MUST detect this distinction accurately and report `RequiresDependency` (with HIP 7.2 guidance) rather than claiming Neural Rendering is ready out of the box.
  - **Overlay & Anti-Cheat:**
    - In-game overlay defaults to the **`End`** key (Up/Down/Left/Right/Enter/PgUp/PgDn).
    - Games with active kernel anti-cheat block proxy DLL loading; must only be used in offline/single-player modes or games without anti-cheat enforcement.

---

### 1.2 Game Management Reference — `Wehaveall/AMDLSS5`
- **Repository:** `https://github.com/Wehaveall/AMDLSS5`
- **Inspected Release:** `v2.0.0` (`AMDLSS5 2.0.0`), published `2026-11-06T18:02:19Z`. Asset: `AMDLSS5-2.0.0.zip` (`328,488,898` bytes). Note: source tree currently holds the `1.6.1` source (`instalador/src/*.cs` and `src/`).
- **Hardware Exclusion (Critical):**
  - Explicitly targets RDNA 4 (RX 9070 / 9070 XT supported; RX 9060 untested) and RDNA 3 (RX 7000 alpha).
  - **Explicitly excludes RX 6000 (RDNA 2) and older GPUs / integrated graphics.** Therefore, Project Luma **never** installs or offers `AMDLSS5` as a rendering backend for the RX 6600.
- **Architectural Patterns Studied (C# Installer `instalador/src/`):**
  - **`Lojas.cs`:** Independent multi-store discovery across Steam (`HKCU\Software\Valve\Steam`, `libraryfolders.vdf`, `appmanifest_*.acf`, path-traversal guard on `installdir`, filtering tool AppIDs like `228980`), Epic Games (`%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`), GOG Galaxy (`HKLM\SOFTWARE\GOG.com\Games`), Ubisoft Connect (`HKLM\SOFTWARE\Ubisoft\Launcher\Installs`), EA App & Battle.net (Uninstall registry keys filtered by Publisher), and Xbox / Microsoft Store (`.GamingRoot` + `XboxGames\<Title>\Content`).
  - **`Pe.cs` & `Motor.cs` (`Detector`):** Lightweight PE32/PE32+ header reader verifying x64 architecture, Import Directory entries (`version.dll`, `d3d12.dll`, `dxgi.dll`), scoring candidate executables up to depth 3 while ignoring redist/crash/anti-cheat subfolders, and detecting `amd_fidelityfx_dx12.dll` vs split `loader + upscaler` vs `ffx_fsr2*`.
  - **`Classificacao.cs`:** Anti-cheat scanning (`EasyAntiCheat`, `BattlEye`, `EAAntiCheat`, `GameGuard`, `XIGNCODE`, `PunkBuster`, `mhyprot`, `start_protected_game.exe`, and `WindowsApps` protection), plus 3-tier classification (`Tested`, `NotTested`, `NotSupported`).
  - **`Motor.cs` (`Sessoes` & `Ini`):** Transactional session backup (`acoes.json` written atomically via `.tmp` + `File.Replace`), running process check via `OpenProcess` + `QueryFullProcessImageNameW` scoped to the game folder, hash-verified uninstall that preserves third-party files, and encoding-safe non-destructive INI merging.

---

### 1.3 Upscaling & Frame Generation Backend — `OptiScaler/OptiScaler`
- **Repository:** `https://github.com/OptiScaler/OptiScaler`
- **Inspected Release:** `v0.9.5` (`v0.9.5-final`, maintenance branch final release, signed via SignPath Foundation), published `2026-11-16T19:17:35Z`. Asset: `Optiscaler_0.9.5-final.20261116.7z` (`70,418,897` bytes).
- **License:** `GPL-3.0`.
- **Bundled Components in `v0.9.5`:**
  - `OptiScaler 0.9.5-final` (SignPath code-signed)
  - `Fakenvapi 1.4.1` (Reflex hooking -> AMD Anti-Lag 2 on RDNA 1+, LatencyFlex, or Intel XeLL)
  - Nukem's `dlssg_to_fsr3 0.130` (converts native DLSS-FG inputs to FSR 3 FG)
  - **AMD FidelityFX SDK 2.4:** **FSR 4.1.2** upscaling + **FSR-FG 4.0.2**
  - **Intel XeSS 3.0.2 SDK:** XeSS 2.0.2 upscaling + XeFG + XeLL
- **Upscaling & RDNA 2 Significance:**
  - In `v0.9.5`, AMD FidelityFX SDK 2.4 adds **official INT8 support for RDNA 2 (`FSR4-i8-2`)** and Ryzen APUs (`FSR4L-i8`), in addition to RDNA 3 (`FSR4-i8`) and RDNA 4 (`FSR4-Upscale` FP8).
  - Supports **DX12** (XeSS, FSR 2.1.2/2.2.1, FSR 3.x/4.x, DLSS), **DX11** (native FSR 2.2.1/3.1.2, or DX12 backends via `dx11on12`), and **Vulkan** (native FSR 2/3, XeSS 2.x, native FSR 4.1.2 Vulkan WIP or `VKon12`).
  - Controlled via `OptiScaler.ini` (`Dx12Upscaler`, `Dx11Upscaler`, `VulkanUpscaler`, `RRUpscaler`, `[FrameGen] Enabled`, `FGInput`, `FGOutput`, `FGNvngxReplacement`, `Fsr4ForceEnableInt8`, `Fsr4EnableWatermark`, `RCAS`, `MAS`) and in-game overlay (`Insert` key, stats via `PageUp`/`PageDown`).
- **Frame Generation (`OptiFG` + `FSR-FG` + `dlssg-to-fsr3`):**
  - `OptiFG` works in **DX12 only** and requires HUDfix (`DrawUIOverFG=true`) on FSR 3 FG to prevent UI ghosting.
  - `FSR-FG 4.0.2` adds HW Flip Metering (Driver-Assisted Frame Pacing) on RDNA 4 with Adrenalin `26.10.1+`.
  - **Rollback:** Clean proxy removal (`dxgi.dll`, `version.dll`, `winmm.dll`, `OptiScaler.ini`, and companion DLLs) restores the game to its exact pre-mod state.

---

### 1.4 Official AMD Documentation & Runtime Ecosystem
1. **AMD FidelityFX SDK 2.4 & FSR 4.1.2 (`gpuopen.com`):**
   - Extends ML-based FSR Upscaling 4.1.2 via **INT8** quantized models (`FSR4-i8-2`) to discrete **RDNA 2 (RX 6000)** desktop GPUs and RDNA 3 (`FSR4-i8`), plus a compact Lite INT8 model (`FSR4L-i8`) for Ryzen APUs/handhelds, while RDNA 4 executes the native FP8 model.
   - Integrates through the unified `amd_fidelityfx_dx12.dll` / `amd_fidelityfx_loader_dx12.dll` + `amd_fidelityfx_upscaler_dx12.dll` API, enabling driver-level Adrenalin upgrades.
2. **AMD FSR Frame Generation (`FSR-FG 3.1` / `4.0.2`):**
   - Optical-flow / ML-assisted frame generation operating in DX12 games. Requires game integration or a wrapper (`OptiScaler` / `dlssg-to-fsr3`) supplying depth, motion vectors, and HUDless / UI textures.
3. **AMD Fluid Motion Frames 2 (`AFMF 2`):**
   - Driver-level frame generation integrated in AMD Software: Adrenalin Edition (`Gaming -> Graphics -> AMD Fluid Motion Frames` or `HYPR-RX`) for RX 6000 (RDNA 2) and newer across DX11, DX12, Vulkan, and OpenGL.
   - Official requirements: Windows 10/11 64-bit, in-game VSync disabled; RDNA 2 desktop GPUs generally require exclusive Fullscreen mode (Borderless Fullscreen support expanded on RDNA 3+).
   - **No Public Config API:** Project Luma provides **guided configuration** and **double-FG conflict detection** rather than writing undocumented Adrenalin driver registry keys.
4. **AMD HIP Runtime / HIP SDK 7.2 (`rocm.docs.amd.com` & `amd.com`):**
   - Ships `amdhip64_7.dll` (typically under `C:\Program Files\AMD\ROCm\<ver>\bin` or `C:\Windows\System32`).
   - Distinct from `amdhip64_6.dll` (HIP 6.x bundled with Adrenalin drivers). Project Luma inspects both `System32` and `C:\Program Files\AMD\ROCm` and reports exact version availability.

---

## 2. Summary of Verified Decisions for Project Luma

| Backend / Source | Latest Version & Date | RDNA 2 (RX 6600) Role | Redistribution Policy |
|---|---|---|---|
| `danielblnc/DLSS-NR-on-AMD` | `v0.9.0` (`2026-11-18`) / `v0.8.0` (`2026-10-17`) | **Primary Neural Rendering backend** (`Experimental`; requires HIP 7.2 + user `nvngx_dlssnr.dll`). **DLSS-FG is `Unsupported`**. | **Do not bundle or mirror.** Fetch directly via official GitHub release on user request or link to official releases page. |
| `OptiScaler/OptiScaler` | `v0.9.5` (`2026-11-16`) | **Primary Super Resolution & Frame Generation backend** (`Supported`: FSR 4.1.2 INT8 `FSR4-i8-2`, FSR 3.1.2, XeSS 2.0.2, FSR-FG, OptiFG, Nukem `dlssg-to-fsr3`). | **GPL-3.0.** Download official release archive directly from GitHub Releases; preserve license & attribution notices. |
| `Wehaveall/AMDLSS5` | `v2.0.0` (`2026-11-06`) | **Excluded** (RDNA 4 / RDNA 3 alpha only; explicitly excludes RX 6000). | Studied for game store scanning, PE scoring, anti-cheat detection, and transactional backup architecture. |
| Proprietary NVIDIA DLLs (`nvngx_dlss*.dll`) | User-owned (`310.8.0.0+` / `310.9.16+`) | User-imported from legitimate owned games when required by DLSS-NR-on-AMD. | **Never downloaded, bundled, or redistributed.** Validated locally via PE64 + Version + SHA-256 vault. |
