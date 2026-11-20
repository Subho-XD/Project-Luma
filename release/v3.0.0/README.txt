Project Luma v3.0.0 — Windows x64 Release
=======================================

1. What Project Luma Does
Project Luma is a Windows gaming enhancement launcher for AMD Radeon GPUs (RDNA 1–4)
that detects your GPU, discovers installed PC games, recommends enhancement presets
with automatic backups, and launches titles cleanly.

2. Windows Requirements
- Windows 10 / Windows 11 (64-bit x64)
- Supported AMD Radeon GPU for enhancement features (baseline tested on RX 6600 / gfx1032)

3. Installation Instructions
Run ProjectLuma-Setup-x64.exe interactively or with silent flags:
  .\ProjectLuma-Setup-x64.exe --silent --install-dir "%LocalAppData%\Programs\ProjectLuma"

Note: In v3.0.0, keep the adjacent payload folder staged during offline install or see v3.1.0 for standalone self-extracting installer packaging.

4. First-Launch Instructions
Open Project Luma -> wait for GPU & library scan -> select a game -> Enhance -> Play.

5. Supported AMD GPU Information & Limitations
- RDNA 2 (RX 6000 / gfx1032): Super Resolution (FSR 4.1.2 INT8 / FSR 3.1.2) and Frame Generation supported. Experimental Neural Rendering requires community DLSS-NR-on-AMD + user-supplied nvngx_dlssnr.dll (>= 310.8.0.0 HT / >= 310.9.16.0 CC). Official AMD HIP SDK 7.2 docs exclude gfx1032.
- Ray Reconstruction requires RDNA 3 (RX 7000) or RDNA 4 (RX 9000).

6. License Compliance & User-Owned DLLs
Project Luma never redistributes proprietary NVIDIA DLLs or non-redistributable DLSS-NR-on-AMD binaries.
Upstream official release page: https://github.com/danielblnc/DLSS-NR-on-AMD/releases
