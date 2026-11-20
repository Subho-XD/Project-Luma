# Project Luma — Backend Integration & Architecture Plan

## 1. Architectural Principles
1. **Strict Separation of Concerns:** Rendering backend adapters (`ProjectLuma.BackendManager`), package verification (`ProjectLuma.PackageManager`), hardware detection (`ProjectLuma.GpuDetection`), compatibility rules (`ProjectLuma.Compatibility`), game scanning (`ProjectLuma.GameDiscovery`), and transactional file operations (`ProjectLuma.GameInstaller`) are completely decoupled from `ProjectLuma.UI`.
2. **Modular Four-Pillar Rendering Pipeline:** Neural Rendering, Super Resolution, Frame Generation, and Ray Reconstruction operate as independent modules with independent capability checks, presets, and conflict guards.
3. **Centralized Staging + Thin Per-Game Deployment:** Downloaded release assets live under `%LocalAppData%\ProjectLuma\backends\<backend-id>\<version>\`. Previous versions remain preserved in `archive\` for instant rollback.

---

## 2. Backend Adapter Contracts (`ProjectLuma.Core`)

Every rendering technology implements `IRenderingBackendAdapter`:
- `string BackendId` (`dlss-nr-on-amd`, `optiscaler`, `amd-afmf-guide`)
- `RenderingFeatureType FeatureType` (`NeuralRendering`, `SuperResolution`, `FrameGeneration`, `RayReconstruction`)
- `FeatureCapabilityEvaluation EvaluateCapability(DetectedGpuInfo gpu, DiscoveredGame? game, IReadOnlyCollection<UserImportedDllRecord> userDlls, UpdateChannel channel)`
- `BackendInstallPlan BuildInstallPlan(DiscoveredGame game, GameEnhancementProfile profile, string stagedBackendPath, string userDllVaultPath)`
- `void ValidateInstalledFiles(string gameExeDir, GameEnhancementProfile profile)`

---

## 3. Concrete Backend Integrations

### 3.1 Neural Rendering (`backends/NeuralRendering/`)
- **Adapter:** `DlssNeuralRenderingAdapter` (`dlss-nr-on-amd`)
- **Upstream Source:** `https://api.github.com/repos/danielblnc/DLSS-NR-on-AMD/releases`
- **RX 6600 (RDNA 2) Behavior:**
  - Reports base capability `Experimental` (`RDNA 2 HIP path`) and checks for **both** `amdhip64_7.dll` (HIP 7.2) and user-imported `nvngx_dlssnr.dll` (`>= 310.8.0.0` for HT model, `>= 310.9.16.0` for v0.9.0 CC model).
  - If either prerequisite is absent, surfaces `RequiresDependency` with direct instructions while displaying the exact RDNA 2 experimental status.
  - Exposes `Fast` and `Superfast` (v0.9.0) quality profiles on RX 6000 and marks `Reference` mode as disabled/unavailable on RDNA 2.
  - Writes non-destructive INI settings (`dlssnr_on_amd.ini`) preserving user overrides and sets overlay hotkey (`End`).

### 3.2 Super Resolution (`backends/Upscaling/`)
- **Adapter:** `OptiScalerSuperResolutionAdapter` (`optiscaler-sr`)
- **Upstream Source:** `https://api.github.com/repos/OptiScaler/OptiScaler/releases`
- **RX 6600 (RDNA 2) Behavior:**
  - Reports `Supported` via OptiScaler `v0.9.5` (FidelityFX SDK 2.4).
  - Exposes verified upscaler outputs:
    1. **AMD FSR 4.1.2 INT8 (`FSR4-i8-2`)** (`Dx12Upscaler=ffx`, `Fsr4ForceEnableInt8=auto`)
    2. **AMD FSR 3.1.2 / 2.3.2** (`Dx12Upscaler=ffx` or `fsr22`)
    3. **Intel XeSS 2.0.2 (SDK 3.0.2)** (`Dx12Upscaler=xess`)
  - Exposes only presets supported by the chosen upscaler (`UltraQuality`, `Quality`, `Balanced`, `Performance`, `UltraPerformance`, `NativeAA`), plus RCAS/MAS sharpness configuration and optional FSR 4 Watermark verification toggle (`Fsr4EnableWatermark`).

### 3.3 Frame Generation (`backends/FrameGeneration/`)
- **Adapter:** `FrameGenerationAdapter` (`optiscaler-fg` & `amd-afmf-guide`)
- **Double Frame Generation Prevention:**
  - Inspects `DiscoveredGame.NativeFrameGenSupported` (`amd_fidelityfx_framegeneration_dx12.dll`, `nvngx_dlssg.dll`, `sl.dlss_g.dll`).
  - If native FG or OptiFG is active, blocks/warns against simultaneously enabling driver-level AFMF 2 or stacking Nukem `dlssg-to-fsr3` on top of native FSR-FG.
- **RX 6600 (RDNA 2) Behavior:**
  - **FSR Frame Generation 4.0.2 / OptiFG (DX12 + HUDfix `DrawUIOverFG=true`) / Nukem `dlssg-to-fsr3`:** `Supported` on DX12 titles.
  - **AMD Fluid Motion Frames 2 (AFMF 2):** `SupportedWithLimitations` via **Guided Adrenalin Configuration** (Fullscreen mode + in-game VSync OFF; Project Luma never writes undocumented driver registry entries).
  - **DLSS Frame Generation (`DLSS-NR-on-AMD`):** Explicitly gated as `Unsupported` on RX 6000 (RDNA 2).

### 3.4 Ray Reconstruction (`backends/Reconstruction/`)
- **Adapter:** `RayReconstructionAdapter` (`dlss-rr-on-amd`)
- **RX 6600 (RDNA 2) Behavior:**
  - On Stable channel (`v0.8.0` baseline): Explicitly marked `Unsupported` with technical explanation ("DLSS Ray Reconstruction Model F requires RDNA 3/4 hardware and a DX12 ray-traced game with DLSS-RR inputs; RDNA 2 uses standard Super Resolution").
  - Never conflates Super Resolution with Ray Reconstruction.

---

## 4. Transactional Installation & Self-Updating Pipeline

1. **Pre-Install Verification:** Confirm x64 PE, writable target directory, zero running processes inside game folder (`OpenProcess` + `QueryFullProcessImageNameW`), and zero active anti-cheat blockers.
2. **Transactional Backup (`<GameExeDir>\.luma_backup\<SessionId>\manifest.json`):** Record original SHA-256 of any existing files, copy originals into the session backup folder, deploy managed files, merge INIs safely, and re-verify SHA-256 on disk.
3. **Update Coordinator (`BackgroundService`):** Runs on startup + configurable interval (default 6h) with GitHub ETag caching, rate-limit backoff, offline resilience, SHA-256 verification, staged installation wait-for-exit guard, and standalone `ProjectLuma.Updater.exe` atomic swap with `--health-check` rollback.
