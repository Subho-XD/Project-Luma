Project Luma v3.3.2 — AMD Gaming Enhancement Launcher
==========================================================

1. What Project Luma Does
Project Luma is a Windows gaming enhancement launcher for AMD Radeon GPUs (RDNA 1 through RDNA 4).
It detects your GPU hardware, discovers installed PC games and artwork automatically, applies beginner-friendly enhancement profiles with automatic backups, and launches titles cleanly through their native store or executable.

2. Windows Requirements
- Windows 10 / Windows 11 (64-bit x64)
- Supported AMD Radeon graphics card (tested baseline: AMD Radeon RX 6600 / RDNA 2 / gfx1032)
- Self-contained .NET 10 runtime is bundled directly inside Project Luma (no separate download required)

3. Installation Instructions
Double-click ProjectLuma-Setup-x64.exe for the 3-stage interactive setup wizard (Welcome -> System Check -> Install & Finish), or install unattended:
  .\ProjectLuma-Setup-x64.exe --silent --install-dir "%LocalAppData%\Programs\ProjectLuma"

4. First-Launch Instructions
Open Project Luma -> wait for GPU & library scan -> select any game -> click Enhance -> click Play.
To view technical parameters, expand "Advanced Settings (For Enthusiasts)" inside a Game Hub.

5. Supported AMD GPU Information
- RDNA 2 (RX 6000 / gfx1032): Image Quality (FSR 4.1.2 INT8 / FSR 3.1.2) and Gameplay Smoothness (FSR-FG / OptiFG DX12 / Nukem dlsg-to-fsr3) are supported.
- Neural Rendering (AI Visuals) on RDNA 2 is an experimental community path. Note that AMD's official Windows HIP SDK 7.2 documentation explicitly excludes RX 6600 / gfx1032; experimental RDNA 2 Fast and Superfast modes rely on community runtime compatibility, while Reference mode is unavailable.
- Ray-Traced Effects (DLSS Ray Reconstruction) requires RDNA 3 (RX 7000) or RDNA 4 (RX 9000).
- AMDLSS5 excludes RX 6000 (RDNA 2) GPUs and is never offered on incompatible GPUs.

6. How Updates Work
Open "Downloads & Updates" in the sidebar to check upstream GitHub releases for eligible open-source components (OptiScaler) or verify local integrations. Project Luma never displays false install buttons for components that are already current or unsupported on your GPU.

7. How to Uninstall
Open Windows Settings -> Apps -> Installed apps -> "Project Luma", or run "Uninstall Project Luma" from the Start Menu, or run ProjectLuma-Uninstall.exe inside the installation folder. The Maintenance Utility lets you repair the installation, restore backed-up game files cleanly using SHA-256 verification, keep backup archives, clear local cache/preferences, and export diagnostics.

8. Missing Licensed Rendering Dependencies & Authorized Sources
Project Luma NEVER bundles, mirrors, or redistributes proprietary NVIDIA DLLs or custom non-commercial binaries.
- DLSS-NR-on-AMD (Custom Personal / Non-Commercial License): Download only from the official upstream GitHub release page:
  https://github.com/danielblnc/DLSS-NR-on-AMD/releases
- Note: The DLSS-NR-on-AMD GitHub release page does NOT include proprietary NVIDIA model DLLs. When optional AI Visuals require nvngx_dlssnr.dll (>= 310.8.0.0 HT / >= 310.9.16.0 CC), use a legitimate local copy already installed with a licensed game or authorized SDK on your PC. Do not download DLLs from unofficial third-party DLL websites.
- OptiScaler (GPL-3.0): Official releases are fetched directly from:
  https://github.com/OptiScaler/OptiScaler/releases

9. Missing Neural Rendering Prerequisites
Open Enhancement Library -> AI Neural Rendering Engine -> View details -> NVIDIA Neural Rendering Model to:
- Import a legitimate local nvngx_dlssnr.dll (>= 310.9.16.0 for CC model, >= 310.8.0.0 for HT model) via the native Windows file picker.
- Scan installed games or your Downloads folder cleanly (bounded recursion depth <= 3, cancellable).
- Open the local DLL vault (%LocalAppData%\ProjectLuma\UserComponents\NVIDIA\DLSS-NR\<version>), verify SHA-256 / PE64 / version metadata, or remove an imported copy.
- Inside a Game Hub, click "Find Required Components" to run 7-stage AI Visuals discovery and stage components for supported NeuralHub integration.

10. Official Upstream Source Links
- OptiScaler: https://github.com/OptiScaler/OptiScaler/releases
- DLSS-NR-on-AMD: https://github.com/danielblnc/DLSS-NR-on-AMD/releases
- AMD HIP SDK Docs: https://www.amd.com/en/developer/resources/rocm-hub/hip-sdk.html

11. Known Experimental Limitations
- Official AMD HIP SDK 7.2 Windows docs exclude RDNA 2 / gfx1032.
- DLSS Frame Generation via DLSS-NR-on-AMD v0.9.0 is unavailable on RX 6000 (RDNA 2); Project Luma disables that path automatically and directs users to built-in FG, OptiFG DX12, or Adrenalin AFMF 2.
- Games with active kernel anti-cheat are blocked from file injection by default for account safety unless offline single-player mode is explicitly confirmed.
- Launching online-protected games starts the unmodified native game safely.

12. Support & Troubleshooting
- Open Settings -> Recovery -> Open Backups Folder or Export Diagnostics...
- Review logs inside %LocalAppData%\ProjectLuma\logs or inspect SHA256SUMS.txt in this release directory before running ProjectLuma-Setup-x64.exe.