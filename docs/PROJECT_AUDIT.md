# Project Luma — Existing Project Audit (`v2.0.0 -> v3.0.0`)

**Audit Date:** 2026-11-20  
**Reference Test Hardware:** AMD Radeon RX 6600 (`VEN_1002&DEV_73FF`, RDNA 2 / `gfx1032`, 7.96 GB VRAM), AMD Ryzen 5 5500, 32 GB RAM, Windows 11 Pro Build `26300`.  
**Baseline Archive Created Before Changes:** `backups/ProjectLuma-PreV3-Backup.zip`.

---

## 1. Framework & Solution Structure
- **Runtime / UI Stack:** C# / .NET 10 (`net10.0-windows`), WPF (Hardware-Accelerated Direct3D 9 / DWM Composition), MVVM.
- **Projects Audited (`14` total in `ProjectLuma.sln`):**
  1. `ProjectLuma.Core` — Enums, domain models, interfaces.
  2. `ProjectLuma.Logging` — Structured JSONL + plain-text diagnostics logger (`%LocalAppData%\ProjectLuma\logs`).
  3. `ProjectLuma.GpuDetection` — DXGI (`IDXGIAdapter1::GetDesc1`) + Display Registry + WMI + PCI database (`amd_gpus.json`) GPU detector.
  4. `ProjectLuma.Compatibility` — SQLite compatibility repository + 7-state `CapabilityEngine` + preset engine (`ApplyPreset`).
  5. `ProjectLuma.GameDiscovery` — Multi-store scanner (`Steam`, `Epic`, `GOG`, `Ubisoft`, `EA`, `Battle.net`, `Xbox`), safe binary PE32+ inspector (`PeAnalyzer`), and artwork resolver (`ArtworkDiscoveryService`).
  6. `ProjectLuma.PackageManager` — `GitHubPackageManager`, `UserDllVault`, and `IntelligentComponentResolver`.
  7. `ProjectLuma.BackendManager` — Modular backend plan composers (`OptiScaler`, `DLSS-NR-on-AMD`, `AFMF 2`, `AMDLSS5` reference).
  8. `ProjectLuma.GameInstaller` — Transactional installer with SHA-256 verification, `.luma_backup` manifests, non-destructive INI merging, and byte-exact rollback.
  9. `ProjectLuma.UpdateService` — Background hosted update check coordinator.
  10. `ProjectLuma.UI` — Views, ViewModels, Themes (`LumaTokens.xaml`, `Icons.xaml`), Converters, Controls.
  11. `ProjectLuma.App` — Dependency Injection host + `ProjectLuma.exe`.
  12. `ProjectLuma.Updater` — Atomic self-updater with health check & rollback.
  13. `ProjectLuma.Setup` — Windows Setup wizard (`ProjectLuma-Setup-x64.exe`).
  14. `ProjectLuma.Tests` — Automated xUnit test suite (`22 / 22` passing prior to v3.0.0).

---

## 2. Functional & Design Gaps Found
1. **Brand Identity & Visual System:**
   - Currently uses placeholder triangle icon (`IconPrism`) and blue/cyan (`#38BDF8`) color scheme rather than the user's supplied white-ghost-on-AMD-red icon and AMD Radeon (`#ED1C24`, `#101010`, `#181818`, `#202020`, `#292929`) theme.
   - Uses `Space Grotesk` / `Manrope` typography tokens rather than bundled **Google Sans** + **Space Mono** under SIL OFL.
2. **Download Official UI Freeze & Timeout Crash Risk (`GitHubPackageManager.cs`):**
   - `HttpClient.Timeout` is set to `30 seconds` for full archive downloads (`Optiscaler_0.9.5-final.20261116.7z` is `70.4 MB`), risking premature timeout exceptions.
   - `ExtractSevenZipSafely` uses random-access iteration (`foreach (var entry in archive.Entries)`) on solid LZMA2 `.7z` archives synchronously without offloading or per-component concurrency locks, causing UI responsiveness drops or hangs.
   - Missing download speed, downloaded bytes, state machine enums (`Downloading`, `Verifying`, `Installing`, `Failed`), resumable `.part` downloads, retry backoff, and cancellation cleanup.
3. **RX 6600 (`gfx1032`) HIP & Neural Rendering Accuracy:**
   - Needs explicit `gfx1032` target mapping and clear disclosure that AMD's official HIP SDK 7.2 Windows docs exclude RX 6600 (`gfx1032`), while `DLSS-NR-on-AMD` Alpha `v0.9.0` (`2026-11-18`) offers an experimental HIP path requiring `nvngx_dlssnr.dll >= 310.9.16.0` (for CC model) or `>= 310.8.0.0` (for HT) and `amdhip64_7.dll`.
   - Automatic scanning of known installed game folders for `nvngx_dlssnr.dll` (`IUserDllVault.ScanGameLibrariesForUserDllsAsync`) was missing.
   - `DLSS-NR-on-AMD` license prohibits redistribution/bundling/mirroring; needs safe existing-installation detection/linking alongside official upstream links.
4. **Missing Direct Game Launching & Optional In-Game Notification:**
   - `My Games` and `Game Details` lack a store-aware **PLAY** workflow (`Steam` protocol / verified executable launch, running check, pre-launch file verification) and an optional click-through **Project Luma Launch Notification** reflecting verified active enhancements.
5. **Onboarding, Installer & Uninstaller Completeness:**
   - First-run onboarding uses 6 screens instead of a concise 3-step guide (`Meet Project Luma`, `Find Your Games`, `Ready to Play`).
   - Installer uses 5 blue-themed screens instead of 3 crisp AMD-red consumer stages (`Welcome`, `System Check`, `Install & Finish`).
   - Missing full Windows Uninstaller registered under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectLuma` with Start Menu uninstaller shortcut, safe game-file restoration choices, backup preservation toggle, diagnostics export, and maintenance/repair UI.
