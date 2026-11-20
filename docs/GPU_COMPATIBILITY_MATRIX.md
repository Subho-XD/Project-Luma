# Project Luma — AMD GPU Architecture & Capability Compatibility Matrix

Project Luma determines GPU architecture via **PCI Vendor & Device ID hardware lookup** (`DXGI IDXGIAdapter1::GetDesc1` + Windows Registry `MatchingDeviceId`), never by fragile marketing-name string matching alone.

## 1. Local Baseline Device Verification
- **GPU Model:** AMD Radeon RX 6600
- **PCI Identifier:** `VEN_1002` / `DEV_73FF` (`SUBSYS_05D51043`, `REV_C7`)
- **Architecture:** `RDNA2` (Navi 23)
- **Dedicated VRAM:** `8,573,157,376` bytes (`8.0 GB` GDDR6; read via 64-bit DXGI & Registry `qwMemorySize`, avoiding the 4 GB `uint32` `Win32_VideoController.AdapterRAM` truncation)
- **Driver:** WDDM `32.0.21045.29003` (Adrenalin 26.x)
- **CPU:** AMD Ryzen 5 5500 (`AVX2 = Supported`)
- **HIP Runtime Probe:** `amdhip64_6.dll` (`10.0.3652.0`) present; `amdhip64_7.dll` (HIP SDK 7.2) not yet present -> Neural Rendering accurately reports `RequiresDependency` (with `Experimental` hardware capability).

---

## 2. Architecture Matrix Across All Four Rendering Technologies

| Architecture | Series & Examples | Representative PCI Device IDs (`VEN_1002`) | Neural Rendering (`DLSS-NR-on-AMD`) | Super Resolution (`OptiScaler` / `FSR` / `DLSS-SR`) | Frame Generation (`OptiScaler` / `FSR-FG` / `AFMF` / `DLSS-FG`) | Ray Reconstruction (`DLSS-RR`) |
|---|---|---|---|---|---|---|
| **GCN / Vega / Polaris** | RX 400/500, Vega 56/64 | `67DF`, `687F` | **Unsupported** | **SupportedWithLimitations** (FSR 2.2 / FSR 3.1 only; no FSR 4 INT8) | **SupportedWithLimitations** (OptiFG / FSR3-FG DX12; no AFMF 2) | **Unsupported** |
| **RDNA 1** | RX 5700 XT, 5700, 5600 XT, 5500 XT | `7310`, `7312`, `731F`, `7340`, `7341` | **Unsupported** (HIP NN kernels require RDNA 2+) | **SupportedWithLimitations** (FSR 3.1.2, FSR 2.2, XeSS 2.0.2 via OptiScaler; no official FSR 4 INT8) | **SupportedWithLimitations** (OptiFG / FSR3-FG DX12, Nukem `dlssg-to-fsr3`, Anti-Lag 2 via Fakenvapi) | **Unsupported** |
| **RDNA 2 (Baseline: RX 6600)** | RX 6950 XT, 6900 XT, 6800 XT/6800, 6750 XT/6700 XT, **RX 6650 XT/6600 XT/6600**, 6500 XT | `73AF`, `73AE`, `73BF`, `73DF`, `73EF`, **`73FF`**, `743F` | **Experimental** *(or `RequiresDependency` if HIP 7.2 `amdhip64_7.dll` or user `nvngx_dlssnr.dll` missing; `Fast`/`Superfast` modes only, `Reference` disabled)* | **Supported** via OptiScaler v0.9.5 (`FSR 4.1.2 INT8 FSR4-i8-2`, `FSR 3.1.2`, `XeSS 2.0.2`). *(DLSS-SR via DLSS-NR-on-AMD: `Unsupported` on v0.8.0 stable; `Experimental` in v0.9.0 alpha)* | **Supported** via OptiScaler v0.9.5 (`FSR-FG 3.1/4.0.2`, `OptiFG` DX12 + HUDfix, Nukem `dlssg-to-fsr3`). `AFMF 2` `SupportedWithLimitations` (guided, Fullscreen). **`DLSS-FG` is `Unsupported`**. | **Unsupported** *(v0.8.0 stable does not support RDNA 2 RR; v0.9.0 alpha experimental HIP path only if explicitly enabled on experimental channel + user `nvngx_dlssd.dll`)* |
| **RDNA 3** | RX 7900 XTX/XT/GRE, 7800 XT, 7700 XT, 7600 XT/7600 | `744C`, `745E`, `7470`, `747E`, `7480` | **Supported** *(or `RequiresDependency` until `nvngx_dlssnr.dll` imported; native D3D12 mode; HT/CC + Superfast/Fast/Reference)* | **Supported** (`FSR 4.1.2 INT8 FSR4-i8`, `FSR 3.1.2`, `XeSS 2.0.2` via OptiScaler; `DLSS-SR 4.5` models K/J/M/L via DLSS-NR-on-AMD) | **Supported** (`FSR-FG 4.0.2`, `OptiFG`, `Nukem dlssg-to-fsr3`, `AFMF 2` Borderless+FS, and `DLSS-FG` up to x6 MFG via DLSS-NR-on-AMD) | **Supported** *(or `RequiresDependency` until `nvngx_dlssd.dll` imported; requires DX12 RT game with DLSS-RR inputs)* |
| **RDNA 4** | RX 9070 XT, RX 9070, RX 9060 XT, RX 9060 | `7550`, `7551`, `7590`, `7591` | **Supported** *(native D3D12 mode; HT & CC models; Fast & Reference)* | **Supported** (`FSR 4.1.2 FP8 FSR4-Upscale`, `FSR 3.1.2`, `XeSS 2.0.2`, `DLSS-SR 4.5` models K/J/M/L) | **Supported** (`FSR-FG 4.0.2` with HW Flip Metering on Adrenalin 26.10.1+, `OptiFG`, `AFMF 2`, `DLSS-FG` up to x6 MFG) | **Supported** *(DLSS-RR Model F via DLSS-NR-on-AMD; requires DX12 RT game with DLSS-RR inputs)* |
| **Ryzen RDNA iGPUs / APUs** | Radeon 780M/890M/8060S (RDNA 3/3.5 APUs) | `15BF`, `15C8`, `150E`, `1586` | **Unsupported / Experimental** *(Slow iGPU warning: >200ms NR job aborts)* | **SupportedWithLimitations** (`FSR 4.1.2 Lite INT8 FSR4L-i8`, `FSR 3.1.2`, `XeSS 2.0.2`) | **SupportedWithLimitations** (`FSR-FG` / `AFMF 2` Performance Mode; bandwidth-bound) | **Unsupported** |

---

## 3. Capability State Definitions

Project Luma uses a strict 7-state capability taxonomy across hardware, backend, and per-game evaluations:

1. **`Supported`** — Verified compatible on detected GPU architecture, driver, and runtime without blocking missing prerequisites.
2. **`SupportedWithLimitations`** — Functional on detected hardware with documented operational constraints (e.g., AFMF 2 requiring exclusive Fullscreen + disabled VSync on RDNA 2, or RDNA 1 limited to FSR 3.1 without FSR 4 INT8).
3. **`Experimental`** — Supported via community/testing implementation with known visual or stability caveats (e.g., RDNA 2 Neural Rendering on RX 6600; `Reference` quality mode disabled, `Fast`/`Superfast` available).
4. **`RequiresDependency`** — Hardware architecture supports the feature, but a required runtime or user-supplied file is absent (e.g., `amdhip64_7.dll` HIP SDK 7.2 or user-imported `nvngx_dlssnr.dll`).
5. **`RequiresDriver`** — Installed Adrenalin WDDM driver is older than the minimum required version (e.g., `< 26.1.1` for DLSS-NR-on-AMD, `< 26.10.1` for FSR-FG 4.0.2 Flip Metering).
6. **`RequiresGameIntegration`** — Hardware and backend support the feature, but the selected game lacks the required API/inputs (e.g., DX11-only title for DX12 OptiFG/DLSS-NR, or missing native Ray Reconstruction inputs).
7. **`Unsupported`** — Not supported on the detected GPU architecture, selected channel, or game binary (e.g., `DLSS-FG` on RX 6600 RDNA 2, `DLSS-RR` on RX 6600 stable baseline, `AMDLSS5` on RX 6000, or 32-bit / anti-cheat-blocked games).
