# Project Luma — Third-Party Licenses & Compliance Inventory

Project Luma enforces strict license hygiene and binary provenance rules. No proprietary third-party binaries or license-restricted installers are silently bundled or mirrored inside Project Luma.

---

## 1. Upstream Rendering Backends & Tools

### 1.1 `danielblnc/DLSS-NR-on-AMD` (NeuralHub)
- **Repository:** `https://github.com/danielblnc/DLSS-NR-on-AMD`
- **Inspected Version:** `v0.9.0` (`2026-11-18`) / `v0.8.0` (`2026-10-17`)
- **License Type:** Custom Proprietary Personal Non-Commercial License (`Copyright (c) 2026 Daniel Blanco. All rights reserved.`)
- **Key Obligations:**
  - Clause 1: Permitted for personal, non-commercial use on the user's own machines.
  - **Clause 2 (No Redistribution / Bundling):** Prohibits redistributing, bundling, embedding, or re-uploading the Software in whole or in part inside any third-party tool, launcher, installer, or package. **Must link to `https://github.com/danielblnc/DLSS-NR-on-AMD/releases`**.
  - Clause 3: Prohibits modifying, patching, repackaging, or reverse-engineering the distributed binaries.
  - Clause 4: Prohibits commercial use or charging for access.
- **Compliance in Project Luma:**
  - Project Luma **does not bundle** `NeuralHub_v*_setup.exe` or its DLLs in `ProjectLuma.Setup.exe` or the repository.
  - Project Luma fetches official release metadata from `https://api.github.com/repos/danielblnc/DLSS-NR-on-AMD/releases` and lets the user initiate an official user-machine download/launch or link a locally installed NeuralHub directory.
- **Included MIT Third-Party Notices in `DLSS-NR-on-AMD/THIRD_PARTY.txt`:**
  - Microsoft Detours 4.0.1 (`MIT License`, Copyright Microsoft Corporation)
  - AMD FidelityFX SDK API headers (`ffx_api.h`, `ffx_api_types.h`, `ffx_upscale.h`, `MIT License`, Copyright 2026 Advanced Micro Devices, Inc.)

### 1.2 `OptiScaler/OptiScaler`
- **Repository:** `https://github.com/OptiScaler/OptiScaler`
- **Inspected Version:** `v0.9.5` (`2026-11-16`), signed via SignPath Foundation (`SignPath.io` / `SignPath Foundation`).
- **License Type:** `GNU General Public License v3.0 (GPL-3.0)`
- **Key Obligations:**
  - Redistribution or invocation of GPL-3.0 binaries requires preserving copyright notices, providing the full GPL-3.0 license text (`LICENSE`), and making corresponding source code links available.
  - Project Luma dynamically downloads official release assets directly from `https://api.github.com/repos/OptiScaler/OptiScaler/releases` on the user's machine and installs them cleanly as a discrete managed backend module, preserving OptiScaler's license and attribution files (`FreeType` under FTL, `Fakenvapi`, Nukem `dlssg-to-fsr3`, `AMD FidelityFX SDK 2.4`, `Intel XeSS 3.0.2 SDK`).

### 1.3 `Wehaveall/AMDLSS5`
- **Repository:** `https://github.com/Wehaveall/AMDLSS5`
- **Inspected Version:** `v2.0.0` (`2026-11-06`), source tree `1.6.1`.
- **License Type:** Unspecified repository root license (`""` in GitHub metadata); `instalador/modelos/THIRD_PARTY_NOTICES.txt` cites MIT-licensed components (`XeGTAO` Intel, `AMD FidelityFX SDK`, `SweetFX`, `SSRT3` CDRIN, `REFramework` praydog, `LLVM/Comgr` Apache-2.0 with LLVM Exceptions) and non-commercial research components (`SCRFD` InsightFace).
- **Compliance in Project Luma:** Used solely as research reference for multi-store game detection, PE inspection, anti-cheat rules, and backup session integrity. Not distributed or used as a runtime backend.

### 1.4 Proprietary NVIDIA NGX Runtime Libraries
- **Files:** `nvngx_dlssnr.dll`, `nvngx_dlss.dll`, `nvngx_dlssg.dll`, `nvngx_dlssd.dll`
- **Copyright:** NVIDIA Corporation. Subject to NVIDIA's proprietary license terms.
- **Compliance in Project Luma:**
  - Project Luma **never downloads, hosts, bundles, or redistributes** any proprietary NVIDIA DLLs.
  - Users import their own legitimately obtained copies (e.g., from games they already own) via Project Luma's local **User DLL Import & Validation Vault**, which validates PE64 headers, file version, and SHA-256 checksum locally on disk.

### 1.5 Official AMD Components
- **AMD HIP SDK 7.2 (`amdhip64_7.dll`):** Copyright Advanced Micro Devices, Inc. Users install directly from AMD's official portal (`https://www.amd.com/en/developer/resources/rocm-hub/hip-sdk.html`). Project Luma detects presence/version only.
- **AMD FidelityFX SDK 2.4 (`amd_fidelityfx_*.dll`):** MIT License (`Copyright (C) Advanced Micro Devices, Inc.`), bundled inside official OptiScaler v0.9.5 releases.

---

## 2. Project Luma Application .NET Dependencies

| Package | Purpose | License |
|---|---|---|
| `.NET 10 / WPF` (`Microsoft.WindowsDesktop.App 10.0.15`) | Desktop framework & runtime | MIT |
| `Microsoft.Extensions.Hosting` / `DependencyInjection` | DI container & background update coordinator | MIT |
| `Microsoft.Data.Sqlite` | Local SQLite compatibility & inventory persistence | MIT |
| `System.Management` | WMI fallback diagnostics (`Win32_VideoController`, CPU, OS) | MIT |
| `SharpCompress` / `SevenZip` (or native `.7z` / `.zip` extraction via `System.IO.Compression` + verified extractor) | Archive unpacking for official `.zip` / `.7z` GitHub releases | MIT / MS-PL |
| `xunit` (test project only) | Automated unit and integration tests | Apache-2.0 |

---

## 3. Bundled Typography & Branding Assets
- **Google Sans (`assets/fonts/GoogleSans.ttf`):** Licensed under the **SIL Open Font License 1.1** from official `https://github.com/google/fonts/tree/main/ofl/googlesans`. Full text included at `assets/fonts/OFL-GoogleSans.txt` and `licenses/OFL-GoogleSans.txt`.
- **Space Mono (`assets/fonts/SpaceMono-Regular.ttf`, `SpaceMono-Bold.ttf`):** Licensed under the **SIL Open Font License 1.1** from official `https://github.com/googlefonts/spacemono`. Full text included at `assets/fonts/OFL-SpaceMono.txt` and `licenses/OFL-SpaceMono.txt`.
- **Project Luma Official Icon (`assets/branding/project-luma-icon.svg`, `.png`, `.ico`):** User-supplied Project Luma visual mark (`#ED1C24` backdrop + white ghost silhouette).
