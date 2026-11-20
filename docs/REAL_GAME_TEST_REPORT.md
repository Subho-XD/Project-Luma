# Real-Game Non-Destructive Verification Report (`v3.3.1`)

**Hardware & OS Under Test:**
- GPU: `AMD Radeon RX 6600` (`VEN_1002&DEV_73FF`, `RDNA2` / `Navi 23` / `gfx1032`, `7.96 GB` VRAM)
- Driver / OS: Adrenalin `26.11.2` (WDDM `32.0.21045.29003`), `Microsoft Windows 11 Pro`, `AMD Ryzen 5 5500`
- HIP Probe: `amdhip64_6.dll` (`10.0.3652.0`) initialized cleanly (`HIP API 60450101`, `1` device); `amdhip64_7.dll` absent (`Hip72SdkAvailable = false`)
- Harness Isolation: All inspections and screenshot captures ran read-only against real host NeuralHub/Vault/game directories using isolated `PROJECT_LUMA_APPDATA` (`%TEMP%\luma_stageb_appdata`), modifying zero game files and zero NeuralHub store files.

---

## 1. Passed — Read-Only Host & Real Game Verification (`v3.3.1`)

### A. External NeuralHub v0.9.0 & DLL Vault Verification
- **Store & Runtime Detected:** `HKCU\Software\Danielblnc\NeuralHub` -> `%LOCALAPPDATA%\Danielblnc\NeuralHub\neuralhub.exe` (`v0.9.0`) and `runtime\v0.9.0\` (`.nh_payload=True`, `neuralhub_host.dll=True`, `ffx\amd_fidelityfx_upscaler_dx12.dll=True`, `ffx\amd_fidelityfx_framegeneration_dx12.dll=True`).
- **7-State Headline:** `NeuralHub v0.9.0 — Installed and detected` (displayed green `Installed & Detected` badge in Enhancement Library).
- **Zero-Duplication DLL Check:** `data\index.ini` `[nr]` (`nr-cdfa6c1ec...dll`, `310.9.16.0`, `[HT, CC]`) matched `%LOCALAPPDATA%\ProjectLuma\UserComponents\NVIDIA\DLSS-NR\310.9.16.0\nvngx_dlssnr.dll` byte-for-byte via SHA-256 `CDFA6C1EC784867182164C7836F324F087B5C9F374D0C9FE46F54999FEABF3ED` (`IsVaultSynchronizedWithNeuralHub = true`).

### B. `Bright Memory: Infinite` (`F:\Games\Bright Memory - Infinite\BrightMemoryInfinite\Binaries\Win64`)
- **NeuralHub Registration:** Matched `brightmemoryinfinite_win64_shipp-1d760161` in `games.json` (`proxy=winmm.dll`).
- **Inspected Disk State:** `winmm.dll` stub (`96,768` bytes) -> `IsGameLoaderInstalled=true`, `neuralhub.ini` -> `IsGameConfigured=true`, `dlssnr_on_amd.log` -> `HasRuntimeLogInitialized=true`, `HasOptiScalerProxyConflict=false`.
- **Reported Status:** `NeuralHub loader (winmm.dll), neuralhub.ini, and runtime log verified.` (zero files modified).

### C. `Aniimo` (`F:\SteamLibrary\steamapps\common\Aniimo`)
- **NeuralHub Registration:** Matched `aniimo-25807e6c` in `games.json` (`key=steam:4126040`, `proxy=version.dll`).
- **Inspected Disk State:** Active OptiScaler deployment (`version.dll` `26,052,912` bytes + `OptiScaler.ini`) with no `neuralhub.ini` / `dlssnr_on_amd.log`.
- **Conflict Guard:** Reported `HasOptiScalerProxyConflict=true` and `OptiScaler owns 'version.dll' (24.8 MB) and OptiScaler.ini` — preserved OptiScaler untouched on disk without overwriting.

---

## 2. Simulated / Fixture Coverage

- Mock `Danielblnc\NeuralHub` + Vault SHA-256 sync + `RemoveBackendAsync` external-folder protection + failed `--health-check` rollback + `3.3.0 -> 3.3.1` download/verification/Zip-Slip/429/offline/cancel cases verified across `47 / 47` automated Release tests.
- Prior non-destructive `OptiScaler` deployment, `[FSR]` / `[QualityOverrides]` INI persistence, and clean rollback on both `Aniimo` and `Bright Memory: Infinite` remain verified from `v3.3.0`.

---

## 3. Blocked & Unsupported

- Kernel anti-cheat titles (`PUBG: BATTLEGROUNDS`, `VALORANT`) remain read-only blocked (`DetectedBlocked`).
- `DLSS Frame Generation` via `DLSS-NR-on-AMD v0.9.0`, `Ray-Traced Reconstruction Engine` (`Model F`), `AMDLSS5`, and Neural Rendering `Reference` quality remain disabled on `RX 6600` (`RDNA2` / `gfx1032`).
- `amdhip64_7.dll` (HIP 7.2 SDK) remains absent on host (AMD docs exclude `gfx1032`).

---

## 4. Not Tested

- Live 3D frame-time / FPS benchmarking or shader-compilation capture inside running `Aniimo.exe` or `BrightMemoryInfinite-Win64-Shipping.exe` processes was not executed during this release verification pass.
