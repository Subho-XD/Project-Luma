# Project Luma v3.1.0 — Release Readiness Checklist

**Status:** **READY FOR DISTRIBUTION (`v3.1.0`)**  
**Validation Target:** Windows 11 Pro x64 (`10.0.26300`), AMD Radeon RX 6600 (`gfx1032`, `7.96 GB` VRAM, Adrenalin `26.11.2`), `.NET 10.0.15` (`win-x64` self-contained)

---

| Check Area | Verification Outcome | Artifact / Command Reference |
|---|---|---|
| **Full Solution Release Build** | `0` Errors, `0` Warnings across all `12` workspace projects | `dotnet build ProjectLuma.sln -c Release` |
| **Automated Test Suite** | **30 / 30 Passed (`0` Failed, `0` Skipped, `6.6910 s`)** including WPF STA UI Automation, Downloads watcher, and component actions | `dotnet test ProjectLuma.sln -c Release` |
| **Riot Game Hub UI & Dark Scrollbar** | `YOUR GAMES` rail, `296 px` Game Hub hero + `▶ Play`, single-card enhancement rows, slim 7 px dark scrollbars verified across `720p`–`4K` & `100%–200%` DPI | `docs/screenshots_v3_1/`, `docs/RIOT_GAME_HUB_VISUAL_AUDIT.md` |
| **Standalone Embedded Installer** | Single-file `ProjectLuma-Setup-x64.exe` (`216,314,693` bytes, `471` embedded payload files) verified without external `payload/` directory | `release/v3.1.0/ProjectLuma-Setup-x64.exe --verify-package` |
| **Silent Install / Health / Repair / Uninstall** | Isolated temp install (`5055.3 ms`), self-contained `--health-check` (`1047.7 / 1007.0 ms`), `--repair` (`7049.4 ms`), clean `--uninstall` (`2057.1 ms`) | `docs/TEST_RESULTS.md` |
| **Atomic App Self-Updater** | Staging swap, health-check verification, and automatic rollback self-test exited `0` (`1050.4 ms`) | `ProjectLuma.Updater.exe --verify-self-test` |
| **Prior Release Preservation & Checksums** | `release/v3.0.0/` preserved intact; `release/v3.1.0/` generated via `scripts/build-release.ps1` with verified `SHA256SUMS.txt` | `release/v3.0.0/SHA256SUMS.txt`, `release/v3.1.0/SHA256SUMS.txt` |
| **Licensing & Legal Compliance** | Zero bundled proprietary NVIDIA DLLs or non-redistributable `DLSS-NR-on-AMD` binaries; bundled `Google Sans` & `Space Mono` OFL licenses and third-party notices shipped | `release/v3.1.0/THIRD_PARTY_NOTICES/`, `docs/THIRD_PARTY_LICENSES.md` |
| **Font Integrity Audit** | Zero `Space Grotesk` references across `src/` | `Select-String "Space Grotesk"` (`0` matches) |
