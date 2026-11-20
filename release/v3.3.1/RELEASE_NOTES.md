# Project Luma v3.3.1 — Release Notes

## Highlights in v3.3.1
- **Full External NeuralHub v0.9.0 Detection & Vault Sync:** Automatically detects upstream NeuralHub installations (HKCU\Software\Danielblnc\NeuralHub, %LOCALAPPDATA%\Danielblnc\NeuralHub, untime\v0.9.0\.nh_payload + 
euralhub_host.dll + bundled FFX DLLs), inspects data\index.ini (
vngx_dlssnr.dll), verifies SHA-256 sync against the Project Luma Vault without duplicating files, checks per-game games.json / 
euralhub.ini / dlssnr_on_amd.log vs OptiScaler conflicts safely, and exposes **Open NeuralHub**, **Verify Installation**, **Configure Game**, and **View Diagnostics** controls.
- **GitHub Releases Live Self-Updater:** Downloads & Updates now checks official Subho-XD/Project-Luma GitHub Releases, validates SHA256SUMS.txt / API sha256 digests, streams updates with redirect-host allowlisting and Zip-Slip checks, and swaps binaries atomically via out-of-process ProjectLuma.Updater.exe (with --health-check rollback protection and settings/profile preservation).
- **Retained v3.3.0 Fixes:** Persistent per-game FSR quality presets ([FSR] + [QualityOverrides]), truthful Gameplay Smoothness (Already available in this game + RX 6000 DLSS-FG blocking), Windows Tray + Startup Run sync, and non-blocking Download Official UI.