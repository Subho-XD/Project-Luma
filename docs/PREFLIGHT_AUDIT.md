# Project Luma — Preflight Audit (Loop 0)

**Audit Date:** 2026-11-20  
**Baseline Codebase Version:** `v3.1.0` (with `release/v3.0.0/` and `release/v3.1.0/` preserved)  
**Target Release Version:** `v3.2.0` (`release/v3.2.0/`)  
**Validation Hardware:** AMD Radeon RX 6600 (`PCI\VEN_1002&DEV_73FF`, RDNA 2 / Navi 23, `gfx1032`, `7.96 GB` VRAM, WDDM `32.0.21045.29003`, Adrenalin `26.11.2`), AMD Ryzen 5 5500, Windows 11 Pro x64 (`10.0.26300`)

---

## 1. Backup & Release Preservation Check

- **Pre-refinement backup created:** `backups/ProjectLuma-PreRefinement-v3.1.0.zip` (`7,153,987` bytes) archiving `src/`, `tests/`, `data/`, `backends/`, `assets/`, `scripts/`, `ProjectLuma.sln`, and `README.md`.
- **Immutable release preservation verified:**
  - `release/v3.0.0/` (`ProjectLuma-Setup-x64.exe` `148,209,477` bytes + `README.txt`, `RELEASE_NOTES.md`, `SHA256SUMS.txt`, `THIRD_PARTY_NOTICES/`).
  - `release/v3.1.0/` (`ProjectLuma-Setup-x64.exe` `216,314,693` bytes + `README.txt`, `RELEASE_NOTES.md`, `SHA256SUMS.txt`, `THIRD_PARTY_NOTICES/`).
  - Neither existing release directory will be overwritten. Next semantic version is `3.2.0`.

---

## 2. Baseline Verification & Toolchain Discovery

- **Toolchain:** System PATH `dotnet` resolves only to `.NET 9.0.18` runtimes without SDKs. `.NET 10 SDK (10.0.404)` is installed at `%USERPROFILE%\.dotnet\dotnet.exe` and builds/tests the entire 12-project solution cleanly.
- **Automated Test Baseline:** Executed `& "$env:USERPROFILE\.dotnet\dotnet.exe" test ProjectLuma.sln -c Release`:
  - **Result:** `30 / 30` passed (`0` failed, `0` skipped, duration `7s`).
  - Covers GPU detection, capability matrix, VDF/PE discovery, game installer `.luma_backup`, GitHub package streaming + cancellation + UI STA automation, Downloads watcher, UpdateCoordinator actions, and InstallerEngine unit tests.

---

## 3. Critical P0 Defect Reproduction — Graphical Installer Crash

Launching `release\v3.1.0\ProjectLuma-Setup-x64.exe` interactively without CLI switches failed reproducibly:
- `t=1s..4s`: No window rendered (`MainWindowHandle = 0`) because `App.OnStartup` synchronously extracted the `216 MB` embedded `payload.zip` into `%TEMP%` on the UI thread before constructing `SetupWindow`.
- `t=5s`: Process crashed with exit code `-532462766` (`0xe0434352`).
- **Windows Application Event Log root cause:**
  ```
  System.Windows.Markup.XamlParseException: Provide value on 'System.Windows.Baml2006.TypeConverterMarkupExtension' threw an exception.
  ---> System.ArgumentNullException: Value cannot be null. (Parameter 'path1')
  at System.IO.Path.Combine(String path1, String path2)
  at MS.Internal.AppModel.ContentFilePart.GetStreamCore(FileMode mode, FileAccess access)
  ...
  at ProjectLuma.Setup.SetupWindow.InitializeComponent()
  ```
- **Why CLI tests missed it:** `--verify-package`, `--silent`, `--repair`, and `--uninstall` never instantiate `SetupWindow` or `UninstallWindow`; `--capture-installer-screenshots` ran against a staging or dev environment where `Assembly.Location` / content file resolution behaved differently or had `project-luma.ico` adjacent on disk, whereas the shipped single-file executable (`PublishSingleFile=true`) has `Assembly.Location == ""`.
- **Root fix required:**
  1. Change `project-luma.ico` in `src\ProjectLuma.Setup\ProjectLuma.Setup.csproj` from `<Content>` to `<Resource>` and reference `pack://application:,,,/ProjectLuma-Setup-x64;component/project-luma.ico` in `SetupWindow.xaml` and `UninstallWindow.xaml`.
  2. Eliminate synchronous 216 MB zip extraction on startup so `SetupWindow` and `UninstallWindow` open immediately (`< 500 ms`), deferring/streaming payload extraction cleanly with progress reporting during installation.
  3. Add unhandled exception logging (`%LocalAppData%\ProjectLuma\logs\setup-crash.log`) and execute real Windows UI Automation (UIA) GUI E2E tests against the standalone single-file `ProjectLuma-Setup-x64.exe`.

---

## 4. Visual Reference & Existing UI Audit

### Supplied User Reference Screenshot (`media_1795178380235_c92681f5.jpg`)
- **Left Navigation (~220px):** Quiet dark sidebar (`#141414`), compact brand header at top, clean list of primary destinations (`Home`, etc.) with subtle active pill, plus a quiet lower quick-list of recent/favorite games (no loud uppercase eyebrows, no visual clutter).
- **Main Content (Full remaining width; right-hand social/friends column removed):**
  - Subtle top sub-navigation (`Featured` / filters) and quiet action triggers.
  - **Dominant Panoramic Hero Banner:** Takes ~55% of the primary viewport (`~400px` height at 1080p, rounded `16px` corners), led by full-bleed landscape artwork (`Aniimo`), clear transparent game logo, compact readiness chip (`Tailored for AMD Radeon RX 6600`), concise 1–2 line tagline, prominent AMD-red primary `Play` CTA + secondary `Enhance` button + quiet profile readout beside them.
  - **Horizontal Thumbnail Gallery (`Resume my games`):** Directly beneath the hero, aligned strictly to the hero's left and right edges, displaying wide 16:9 landscape thumbnail cards (`Aniimo`, `Cyberpunk 2077`, `Red Dead Redemption 2`, `Alan Wake 2`) with rounded artwork, centered game logo overlay, minimal title + quick `▶` play and `•••` details icons + quiet status line below each thumbnail, with zero heavy boxed card borders.

### Existing `v3.1.0` UI Defects Found
1. **Structural mismatch:** `ActivePage` defaults to `MyGames` (a portrait card grid below a squeezed `214px` mini-hero) rather than the reference `Home` Hero + horizontal Thumbnail Gallery composition.
2. **Missing separate `Enhancement Library` nav entry & hidden components:** Sidebar lists `My Games`, `Available Enhancements` (generic 4-card overview hiding the actual Component Manager inside a collapsed `<Expander>`), `Downloads & Updates`, `Settings`. Missing `Home` and proper visible `Enhancement Library`.
3. **Inconsistent 4-point spacing & edges:** Views mix `Margin="24,18,24,20"`, `Margin="24,20,24,28"`, `Margin="26,20"`, `Margin="28,0,28,24"`, `Margin="14"` gaps, `CornerRadius="4/5/6/8/10/12"`, and have no `MaxWidth` cap at 1440p/4K.
4. **Typography & Hallmark Floor violations:**
   - Tracked uppercase `FontMono` eyebrows on every screen (`LIBRARY`, `ENHANCEMENTS`, `MANAGEMENT`, `APPLICATION`, `YOUR GAMES`, `ENHANCED PROFILE`, `PREFERENCES & GAME FOLDERS`, `YOUR GAME ENHANCEMENTS`, `YOUR SYSTEM COMPATIBILITY`, `SETUP STAGES`, `DESTINATION FOLDER`).
   - Decorative `<Ellipse>` status dots in `CapabilityBadge.xaml` and sidebar game rows.
   - Nested card-in-card in `GameDetailsView.xaml` (yellow `Optional Setup Required` border inside `YOUR SYSTEM COMPATIBILITY` card).
   - Middle-dot meta chains (`·`) and glued arrows (`←  My Games`, `Continue →`).
5. **Blue/cyan fallback art:** `ArtworkDiscoveryService.WriteProceduralGradientPng` still generates cool blue/cyan gradients (`{24, 96, 138}`, `{34, 82, 122}`) for non-Steam games without local covers (`Alan Wake 2`, `Forza Horizon 5`), violating the AMD red/graphite palette and leaving `Alan Wake 2` without authentic art.

---

## 5. Additional Functional & Upstream Findings

1. **Invalid XAML binding in `EnhancementCentreView.xaml:111`:** `IsEnabled="{Binding IsBusy, Converter={StaticResource BoolToVisibilityConverter}, ConverterParameter=invert}"` binds a `Visibility` enum to a `bool` property.
2. **Downloads & Updates lacks live download progress & independent row concurrency:** `UpdateCentreView.xaml` displays no progress bar, speed, MB progress, or Cancel button during `Download Official`. `AsyncRelayCommand<T>` uses a single global `_running` flag blocking unrelated buttons during a download, and `GitHubPackageManager` lacks per-read stall timeouts and GitHub `asset.digest` SHA-256 verification.
3. **Game Launching (`GameLaunchService.cs`):** Only builds protocol URIs for Steam; Epic/GOG/Ubisoft/EA/Battle.net/Xbox fall back to raw `.exe` (which breaks store DRM auth). Also reports `GameLaunchPhase.Running` immediately upon invoking `steam://` without verifying the game process started.
4. **Neural Rendering & Upstream Verification (Live Check 2026-11-20):**
   - `danielblnc/DLSS-NR-on-AMD` latest release is **`v0.9.0` (Alpha 0.9.0, 2026-11-18)** (`NeuralHub_v0.9.0_setup.exe`, `30,925,039` bytes). Release notes confirm CC model (`nvngx_dlssnr.dll >= 310.9.16`), RDNA 2 `Superfast` + `Fast` modes (Reference unavailable), and `DLSS-FG` unavailable on RX 6000. Its `LICENSE` strictly prohibits redistribution, bundling, mirroring, or embedding in another launcher.
   - Live RX 6600 (`gfx1032`) HIP probe: `C:\Windows\System32\amdhip64_6.dll` (`10.0.3652.0`) loaded and initialized cleanly (`hipRuntimeGetVersion rc=0 ver=60450101`, `hipGetDeviceCount rc=0 count=1`). `amdhip64_7.dll` (HIP 7.2) is NOT installed, and AMD official HIP SDK 7.2 Windows requirements exclude `gfx1032`.
   - `CapabilityEngine.cs` still contains stale `"v0.8.0"` comments/strings, lacks the 8-state `NeuralRenderingActivationState` model (`Available`, `DependencyMissing`, `RuntimeMissing`, `ReadyToConfigure`, `InstallationInProgress`, `Active`, `RuntimeFailed`, `Unsupported`), and disables the AI Visuals toggle in `GameDetailsView` without inline explanation.
5. **OptiScaler Live Check (2026-11-20):** Latest stable release is **`v0.9.5`** (`Optiscaler_0.9.5-final.20261116.7z`, `70,418,897` bytes) with GitHub API digest `sha256:77e3ed76bed04e1fb32bacb5a72799ff172f3da22ed5231c0cf3e84fe6761662`.
6. **Branding & Fonts:** `assets/branding/project-luma-icon.{png,svg}` are identical to `%USERPROFILE%\Downloads\Luma_Icon.{png,svg}`. `GoogleSans.ttf` + `SpaceMono-*.ttf` with SIL OFL-1.1 notices are bundled cleanly; zero `Space Grotesk` references exist in `src/`.
