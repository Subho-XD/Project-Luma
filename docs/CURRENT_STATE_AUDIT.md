# Project Luma — Current State Audit (Pre-Repair Baseline)

**Audit Date:** 2026-11-20  
**Workspace:** `F:\AI\Project Luma`  
**Backup Created:** [ProjectLuma-PreRepair-v3.2.0.zip](file:///F:/AI/Project%20Luma/backups/ProjectLuma-PreRepair-v3.2.0.zip)

---

## 1. Environment & Version Baseline

| Component | Verified Value |
|---|---|
| **Application Framework** | .NET 10 (`net10.0-windows` WPF + `net10.0` class libraries) |
| **SDK Used** | .NET SDK `10.0.404` (`%USERPROFILE%\.dotnet\dotnet.exe`) |
| **Current Source & Installed Version** | `3.2.0` (`ProjectLuma.App.csproj`, `ProjectLuma.Setup.csproj`, `InstallerEngine.cs`, `%LocalAppData%\Programs\ProjectLuma\ProjectLuma.exe` FileVersion `3.2.0.0`) |
| **Host GPU** | `AMD Radeon RX 6600` (`VEN_1002&DEV_73FF`, `RDNA2`, `Navi 23`, `gfx1032`, `7.96 GB` VRAM, WDDM `32.0.21045.29003`, Adrenalin `26.11.2`) |
| **Host HIP Runtime** | `C:\WINDOWS\system32\amdhip64_6.dll` (`10.0.3652.0`, initializes cleanly: `HIP API 60450101`, `1` device); `amdhip64_7.dll` absent; official AMD Windows HIP 7.2 docs exclude `gfx1032` |
| **Git Status** | No `.git` repository initialized yet; `gh` CLI authenticated as `Subho-XD` with no `Project Luma` remote repository present |
| **Automated Test Baseline** | `34 / 34` passing (`dotnet test ProjectLuma.sln -c Release`, 15s) |

---

## 2. Installed Games Verified on Host PC

1. **Aniimo** (`F:\SteamLibrary\steamapps\common\Aniimo\Aniimo.exe`) — Steam AppID `4126040`, Unity, DirectX 12, 64-bit. Contains `nvngx_dlss.dll`, `nvngx_dlssg.dll`, `sl.dlss_g.dll`, `libxess.dll`, plus currently deployed OptiScaler files and `.luma_backup` manifests. Does not include `nvngx_dlssnr.dll`.
2. **Bright Memory: Infinite** (`F:\Games\Bright Memory - Infinite\BrightMemoryInfinite.exe` + `BrightMemoryInfinite\Binaries\Win64\BrightMemoryInfinite-Win64-Shipping.exe`) — Manual install, Unreal Engine, DirectX 12, 64-bit. Has UE plugin `nvngx_dlss.dll`, `nvngx_dlssg.dll`, `sl.dlss_g.dll`, `libxess.dll` inside `Engine\Plugins\Runtime\...`. Does not include `nvngx_dlssnr.dll`.
3. **Anti-Cheat Protected Titles (Do Not Modify):** `PUBG` (`F:\SteamLibrary\steamapps\common\PUBG`) and `VALORANT` (`F:\Games\Riot Games\VALORANT`).

---

## 3. Existing Component & Architecture Audit

### 3.1 Backend Manifests & Local Staging (`data/backend_manifests/backends.json`)
- `optiscaler`: Pinned `v0.9.5` (GPL-3.0, SignPath verified), staged on disk in `%LOCALAPPDATA%\ProjectLuma\backends\optiscaler\v0.9.5`.
- `amd-fsr-pack` (`FFX 2.4 / FSR 4.1.2`), `intel-xess-pack` (`v3.0.2`), `optifg-framegen` (`FSR-FG 4.0.2 / Nukem 0.130`): Marked `BundledWithParent` (`optiscaler`).
- `amd-afmf-guide` (`AFMF-2.1`): `GuidedDriverAdvisor`.
- `dlss-nr-on-amd`: Pinned `v0.9.0` (`Custom-Personal-NonCommercial`, `licenseRedistributionAllowed: false`). **Audit finding:** `%LOCALAPPDATA%\ProjectLuma\backends\dlss-nr-on-amd\v0.9.0\DLSS-NR-on-AMD-0.9.0` contains only GitHub source metadata (`LICENSE`, `README.md`, `THIRD_PARTY.txt`, `.github`) with zero executable/DLL payloads, yet `GitHubPackageManager` and `BackendComponentItem` falsely treat any file in that directory as an installed binary backend.
- `dlss-rr-amd` (`v0.9.0`): Restricted to `RDNA3`, `RDNA4`.
- `amdlss5-reference` (`v2.0.0`): Restricted to `RDNA4`.

### 3.2 Database & Configuration Storage (`SqliteCompatibilityRepository.cs`)
- SQLite DB at `%LOCALAPPDATA%\ProjectLuma\luma.db` contains 7 tables: `AppSettings`, `HardwareProfiles`, `DiscoveredGames`, `GameProfiles`, `BackendManifests`, `UserImportedDlls`, and `UpdateHistory`.
- Per-game `GameEnhancementProfile` JSON records are keyed by `GameId` in `GameProfiles`.

### 3.3 Module-by-Module Functional Findings
1. **NVIDIA Neural Rendering DLL Import (`UserDllVault.cs`, `EnhancementCentreView.xaml`):**
   - `Enhancement Library -> AI Neural Rendering Engine -> View Details` lacks any dedicated NVIDIA Neural Rendering Model section or `Import NVIDIA DLL` workflow.
   - `UserDllVault.ParseVersionPrefix` splits on `','`, which truncates comma-separated NVIDIA `FileVersion` strings (e.g. `"310,6,0,0"` seen in `%LOCALAPPDATA%\ProjectLuma\logs\luma-20261120.log`) down to `"310"`, returning `null` and bypassing version checks and `SupportsCcModel` (`>= 310.9.16.0`) detection.
   - Vault uses a flat `%LOCALAPPDATA%\ProjectLuma\user_dlls\` folder without `<version>\metadata.json` tracking, structured `DllValidationStatus` reporting, or NeuralHub integration detection.
2. **AI Visuals Setup (`GameDetailsView.xaml`, `IntelligentComponentResolver.cs`):**
   - No `Find Required Components` button exists directly in the AI Visuals row.
   - `ExecuteGuidedSetupAsync` silently sets `profile.EnableNeuralRendering = false` when NR dependencies are missing instead of preserving user intent or surfacing the exact failure blocker.
3. **FSR Preset Persistence (`GameDetailsView.xaml`, `MainViewModel.cs`, `CapabilityEngine.cs`, `OptiScalerAdapters.cs`):**
   - `GameDetailsView.xaml` edits `SelectedGame.Profile.SelectedUpscalingPreset` directly on a plain model property with no DB save and no transition to `Custom`.
   - `ReevaluateAllCapabilities()` replaces `card.Profile` and calls `ApplyPreset(..., prevPreset, ...)`, overwriting `SelectedUpscalingPreset` back to `Quality` (Balanced preset) or `Balanced` (SmootherGameplay preset).
   - `OptiScalerSuperResolutionAdapter.cs` writes `QualityRatioUltraQuality` for all presets instead of OptiScaler v0.9.5 `[FSR] Fsr4Preset` and `[QualityOverrides]` per-preset keys.
4. **Gameplay Smoothness (`CapabilityEngine.cs`, `GameCardViewModel.cs`, `GameDetailsView.xaml`, `GameDirectoryInspector.cs`):**
   - When `game.NativeFrameGenSupported` is `true`, `PopulateFriendlyStatuses` sets `SmootherGameplayStatusText = "Ready"` next to a disabled toggle, causing the confusing "Ready badge + greyed-out toggle" defect.
   - `GameDirectoryInspector` only scans depth 2 for nearby DLLs, missing Unreal Engine plugin folders (`Engine\Plugins\Runtime\Nvidia\Streamline\Binaries\ThirdParty\Win64\nvngx_dlssg.dll`) at deeper nesting.
5. **Windows System Tray (`App.xaml.cs`, `MainWindow.xaml.cs`, `SettingsView.xaml`):**
   - `Settings.MinimizeToTray` is persisted in SQLite only; no `NotifyIcon`, no `Closing` cancellation, no `ShutdownMode.OnExplicitShutdown`, no tray context menu, and no `TaskbarCreated` recovery exist.
6. **Start Project Luma with Windows (`MainViewModel.cs`, `SettingsView.xaml`):**
   - `Settings.LaunchOnWindowsStartup` is persisted in SQLite only; verified `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` and Startup folder contain zero Project Luma entries.
7. **Download Official Freezes & Async Safety (`GitHubPackageManager.cs`, `BackendComponentItem.cs`, `MainViewModel.cs`, `UpdateCentreView.xaml`):**
   - `BackendComponentItem.IsInstalled` performs synchronous `Directory.EnumerateFiles(..., AllDirectories)` inside a WPF-bound property getter on the UI thread.
   - `GetManifestsAsync` performs synchronous JSON parsing and directory scanning on the calling thread.
   - `SendWithValidatedRedirectsAsync` shares a 15-minute `HttpClient.Timeout` with no per-request connect/header timeout.
   - `ExtractSevenZipSafelySinglePass` uses blocking `reader.WriteEntryTo(outStream)` rather than cancellable chunked copying.
   - `ApplyUpdateItemCommand` locks out all Downloads actions via `AsyncRelayCommand` and calls `CheckNowAsync()` which clears `UpdateItems` while concurrent operations run.
8. **Installer & Uninstaller (`InstallerEngine.cs`, `SetupWindow.xaml.cs`, `UninstallWindow.xaml.cs`):**
   - `UninstallWindow.OnUninstallClick` triggers uninstallation immediately without an explicit confirmation dialog.
   - Neither installer nor uninstaller maintains or cleans `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ProjectLuma`.
