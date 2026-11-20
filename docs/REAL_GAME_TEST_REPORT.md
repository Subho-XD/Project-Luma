# Real-Game Non-Destructive Test Report (`v3.3.0`)

**Hardware & OS Under Test:**
- GPU: `AMD Radeon RX 6600` (`VEN_1002&DEV_73FF`, `RDNA2` / `Navi 23` / `gfx1032`, `7.96 GB` VRAM)
- Driver / OS: Adrenalin `26.11.2` (WDDM `32.0.21045.29003`), `Microsoft Windows 11 Pro` (Build `26300`), `AMD Ryzen 5 5500`
- HIP Probe: `amdhip64_6.dll` `10.0.3652.0` initialized cleanly (`HIP API 60450101`, `1` device); `amdhip64_7.dll` absent (`Hip72SdkAvailable = false`, `Hip72OfficiallySupported = false`)
- Harness Isolation: Executed via compiled Project Luma Release libraries against isolated temporary SQLite database and backup mirror (`%TEMP%\LumaRealGameTest_v330_*`), using staged `OptiScaler v0.9.5` at `%LOCALAPPDATA%\ProjectLuma\backends\optiscaler\v0.9.5`, leaving user preferences untouched and restoring both tested game directories cleanly afterward.

---

## 1. Passed (Direct Host & Real Game Directory Verification)

### A. `Aniimo` (`F:\SteamLibrary\steamapps\common\Aniimo\Aniimo.exe`)
- **Discovery & Inspection:** Steam (`StoreAppId = 4126040`), `DirectX 12`, `Unity`, `x64`, `SteamLocalCache` artwork (`cover`, `hero`, `logo`), `NativeDlssSupported = true`, `NativeFrameGenSupported = true`.
- **Gameplay Smoothness Evaluation:** Classified as `Case A` (`SmootherGameplayStatusText = "Already available in this game"`, `DoubleFgGuard = true`, `CanToggleFrameGen = false`), providing in-game Frame Generation guidance and suppressing conflicting OptiFG injection.
- **AI Visuals Diagnostics:** Honestly reported `Setup required` (`RuntimeMissing`: `amdhip64_6.dll` active vs missing `amdhip64_7.dll` on `gfx1032`, no NeuralHub v0.9.0 runtime binaries, no imported `nvngx_dlssnr.dll`).
- **FSR Preset Persistence & `OptiScaler.ini`:** Switching `Balanced` -> `Performance` saved to SQLite, reloaded `Preset=Custom, UpscalingPreset=Performance` from a new repository instance, deployed `21` files (`Session 20261120_182242_81bceeed`) via `TransactionalGameInstaller.InstallOrUpdateAsync`, and wrote:
  - `[Upscalers] Dx12Upscaler=ffx`
  - `[FSR] Fsr4Update=true`, `Fsr4ForceEnableInt8=2`, `Fsr4Preset=3`, `Fsr4EnableWatermark=false`
  - `[QualityOverrides] QualityRatioPerformance=2.0`
  - `[ProjectLuma] Profile=Custom`, `UpscalerBackend=optiscaler`, `UpscalerEngine=FSR 4.1.2 INT8 (FSR4-i8-2)`, `QualityPreset=Performance`
  - `[FrameGen]` omitted cleanly because native FG is already available.
- **Health & Rollback:** `VerifyInstallationHealthAsync` returned `Healthy`. `UninstallAsync` rolled back all 21 deployed files and removed empty `D3D12_Optiscaler`, `Licenses`, and `.luma_backup` subdirectories (`LeftoverOptiFilesPresent = False`, `35` top-level files intact).

### B. `Bright Memory: Infinite` (`F:\Games\Bright Memory - Infinite\BrightMemoryInfinite\Binaries\Win64\BrightMemoryInfinite-Win64-Shipping.exe`)
- **Legacy v3.2.0 Cleanup & Unreal Root Resolution:** Restored the prior pre-session backup from `F:\Games\Bright Memory - Infinite` root cleanly (`restored=True`). Re-inspecting `BrightMemoryInfinite-Win64-Shipping.exe` resolved the Unreal root (`F:\Games\Bright Memory - Infinite`) via `GameDirectoryInspector.ResolveInstallRootForExecutable`, detecting `Engine\Plugins\Runtime` (`x64`, `NativeDlssSupported = true`, `NativeXeSsSupported = true`, `NativeFrameGenSupported = true`, `GeneratedFallback` artwork).
- **Detected API & Capability Behavior:** After pre-session `D3D12_Optiscaler` cleanup, static PE inspection on the dual DX11/DX12 UE4 executable detected `d3d11.dll` (`GraphicsApi.DirectX11`; DX12 is selectable via `DX12.lnk`).
  - `Gameplay Smoothness` correctly reported `"Already available in this game"` (`CanToggleFrameGen = false`, `DoubleFgGuard = true`).
  - `AI Visuals` reported `Setup required` noting DX12 is required for `DLSS-NR-on-AMD` (`"Switch BrightMemoryInfinite-Win64-Shipping to DirectX 12 if available. Vulkan and DX11 are not supported by DLSS-NR-on-AMD."`).
- **FSR Preset Persistence, Deployment & Rollback:** Reloaded `Preset=Custom, UpscalingPreset=Performance` from SQLite, deployed `21` files (`Session 20261120_182245_ac6ccbc5`) into `...\Binaries\Win64`, wrote `[Upscalers] Dx11Upscaler=fsr31`, `[FSR] Fsr4Update=true`, `Fsr4ForceEnableInt8=2`, `Fsr4Preset=3`, `[QualityOverrides] QualityRatioPerformance=2.0`, `[ProjectLuma] QualityPreset=Performance` (no `[FrameGen]` section), verified `Healthy`, and rolled back cleanly leaving `LeftoverOptiFilesPresent = False` (`3` clean files in `Win64`: `BrightMemoryInfinite-Win64-Shipping.exe`, `DX12.lnk`, `turbojpeg.dll`).

---

## 2. Simulated (Controlled Fixture Tests Outside Live Game Processes)

- Neural Rendering `nvngx_dlssnr.dll` PE32+ static validation, version thresholds (`CC >= 310.9.16.0`, `HT >= 310.8.0.0`), vault storage (`UserComponents\NVIDIA\DLSS-NR\<version>`), depth `<= 3` discovery, duplicate detection, signature limitation reporting, and AI Visuals 7-stage discovery were verified in [RepairAndReleaseReadinessTests.cs](file:///F:/AI/Project%20Luma/tests/ProjectLuma.Tests/RepairAndReleaseReadinessTests.cs) using version-patched x64 PE32+ and 32-bit PE32 binaries derived from Windows `version.dll`.
- Non-blocking `Download Official` HTTP delays, cancellation, SHA-256 verification, and isolated `HKCU\Software\ProjectLumaTest\*` startup registration were verified via local HTTP handlers and isolated test registry keys.

---

## 3. Blocked (Intentional Safety Protections)

- **`PUBG: BATTLEGROUNDS` & `VALORANT`:** Never modified or injected (`DetectedBlocked` anti-cheat protection policy).
- **Restricted Proprietary/Non-Commercial Binaries:** `DLSS-NR-on-AMD v0.9.0` binaries (`NeuralHub_v0.9.0_setup.exe`) and proprietary `nvngx_dlssnr.dll` are not bundled, downloaded automatically, or deployed by Project Luma without user-acquired files.

---

## 4. Unsupported (Hardware / Driver Limits Verified on RX 6600)

- **AMD HIP SDK 7.2 (`amdhip64_7.dll`):** Absent on host (`Hip72SdkAvailable = false`); AMD's official Windows HIP SDK 7.2 documentation excludes `RDNA 2` / `gfx1032`.
- **DLSS Frame Generation (`DLSS-NR-on-AMD v0.9.0`):** Explicitly unavailable on RX 6000 (`RDNA 2`) per upstream `v0.9.0` release notes (`Unavailable on RX 6000`).
- **DLSS Ray Reconstruction (`Model F`) & `AMDLSS5 Neural Upscaler`:** Hardware-incompatible on RDNA 2 (requires RDNA 3/4 or RDNA 4).
- **Optional Neural Rendering `Reference` Quality Mode:** Unavailable on RX 6000 (`Fast` / experimental RDNA 2 HIP paths only).

---

## 5. Not Tested (Explicitly Unverified Live Runtime Items)

- **Live In-Game Rendered Frames, Shader Compilation, & FPS Benchmarking:** Neither `Aniimo.exe` nor `BrightMemoryInfinite-Win64-Shipping.exe` was launched into live 3D rendering scenes to measure runtime FPS deltas, frametimes, OptiScaler `Insert` overlay rendering, or visual PSNR/artifacts.
- **Full End-to-End Neural Rendering Game Execution via NeuralHub v0.9.0:** Host PC did not have upstream `NeuralHub_v0.9.0_setup.exe` installed or an authorized local `nvngx_dlssnr.dll` (`>= 310.9.16.0`) present, so live `NeuralHub` + HIP execution inside a DirectX 12 game remains unverified at runtime.
