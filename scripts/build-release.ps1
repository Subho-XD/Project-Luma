param(
    [switch]$SkipTests,
    [switch]$AllowOverwriteCurrentVersion
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $Dotnet)) {
    $Dotnet = "dotnet"
}

Write-Host "==> 1. Reading version from ProjectLuma.App.csproj"
[xml]$appProj = Get-Content (Join-Path $Root "src\ProjectLuma.App\ProjectLuma.App.csproj")
$Version = ($appProj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Could not determine Version from ProjectLuma.App.csproj"
}
Write-Host "    Version: v$Version"

$DistDir = Join-Path $Root "dist"
$DistApp = Join-Path $DistDir "ProjectLuma"
$DistInstaller = Join-Path $DistDir "Installer"
$PayloadZip = Join-Path $DistDir "payload.zip"
$ReleaseDir = Join-Path $Root "release\v$Version"

if ((Test-Path $ReleaseDir) -and (-not $AllowOverwriteCurrentVersion)) {
    throw "Release directory '$ReleaseDir' already exists. Increment <Version> or pass -AllowOverwriteCurrentVersion."
}

Write-Host "==> 2. Cleaning temporary staging directories (dist\)"
if (Test-Path $DistDir) {
    Remove-Item $DistDir -Recurse -Force
}
New-Item -ItemType Directory -Path $DistApp, $DistInstaller -Force | Out-Null

Write-Host "==> 3. Building solution in Release configuration"
& $Dotnet build (Join-Path $Root "ProjectLuma.sln") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Release build failed" }

if (-not $SkipTests) {
    Write-Host "==> 4. Running full automated test suite"
    & $Dotnet test (Join-Path $Root "ProjectLuma.sln") -c Release --no-build --nologo --logger "console;verbosity=minimal"
    if ($LASTEXITCODE -ne 0) { throw "Automated test suite failed" }
}

Write-Host "==> 5. Publishing self-contained win-x64 ProjectLuma.App & ProjectLuma.Updater -> dist\ProjectLuma\"
& $Dotnet publish (Join-Path $Root "src\ProjectLuma.App\ProjectLuma.App.csproj") -c Release -r win-x64 --self-contained true -o $DistApp --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish App failed" }

$UpdaterTemp = Join-Path $DistDir "UpdaterTemp"
New-Item -ItemType Directory -Path $UpdaterTemp -Force | Out-Null
& $Dotnet publish (Join-Path $Root "src\ProjectLuma.Updater\ProjectLuma.Updater.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $UpdaterTemp --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish Updater failed" }
Copy-Item (Join-Path $UpdaterTemp "ProjectLuma.Updater.exe") (Join-Path $DistApp "ProjectLuma.Updater.exe") -Force
Remove-Item $UpdaterTemp -Recurse -Force

& (Join-Path $DistApp "ProjectLuma.Updater.exe") --verify-self-test
if ($LASTEXITCODE -ne 0) { throw "Published ProjectLuma.Updater.exe --verify-self-test failed" }

Write-Host "==> 6. Packaging embedded payload.zip & building standalone single-file Installer -> dist\Installer\"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path $PayloadZip) { Remove-Item $PayloadZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($DistApp, $PayloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

& $Dotnet publish (Join-Path $Root "src\ProjectLuma.Setup\ProjectLuma.Setup.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:EmbeddedPayloadZip=$PayloadZip" -o $DistInstaller --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish Setup failed" }

Write-Host "==> 7. Verifying standalone installer package integrity"
$SetupExe = Join-Path $DistInstaller "ProjectLuma-Setup-x64.exe"
$VerifyProc = Start-Process -FilePath $SetupExe -ArgumentList "--verify-package" -Wait -PassThru -NoNewWindow
if ($VerifyProc.ExitCode -ne 0) { throw "Installer --verify-package failed with exit code $($VerifyProc.ExitCode)" }

Write-Host "==> 8. Creating versioned release directory: release\v$Version\"
$NoticesDir = Join-Path $ReleaseDir "THIRD_PARTY_NOTICES"
New-Item -ItemType Directory -Path $NoticesDir -Force | Out-Null

Write-Host "==> 9. Copying release deliverables & third-party notices"
$ReleaseInstaller = Join-Path $ReleaseDir "ProjectLuma-Setup-x64.exe"
$ReleaseZip = Join-Path $ReleaseDir "ProjectLuma-v$Version-win-x64.zip"
Copy-Item $SetupExe $ReleaseInstaller -Force
Copy-Item $PayloadZip $ReleaseZip -Force
Copy-Item (Join-Path $Root "assets\fonts\OFL-*.txt") $NoticesDir -Force
Copy-Item (Join-Path $Root "docs\THIRD_PARTY_LICENSES.md") $NoticesDir -Force

Write-Host "==> 10. Writing release README.txt & RELEASE_NOTES.md"
$ReadmeContent = @"
Project Luma v$Version — AMD Gaming Enhancement Launcher
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
"@
[System.IO.File]::WriteAllText((Join-Path $ReleaseDir "README.txt"), $ReadmeContent, [System.Text.Encoding]::UTF8)

$NotesContent = @"
# Project Luma v$Version — Release Notes

## Highlights in v3.3.1
- **Full External NeuralHub v0.9.0 Detection & Vault Sync:** Automatically detects upstream NeuralHub installations (`HKCU\Software\Danielblnc\NeuralHub`, `%LOCALAPPDATA%\Danielblnc\NeuralHub`, `runtime\v0.9.0\.nh_payload` + `neuralhub_host.dll` + bundled FFX DLLs), inspects `data\index.ini` (`nvngx_dlssnr.dll`), verifies SHA-256 sync against the Project Luma Vault without duplicating files, checks per-game `games.json` / `neuralhub.ini` / `dlssnr_on_amd.log` vs OptiScaler conflicts safely, and exposes **Open NeuralHub**, **Verify Installation**, **Configure Game**, and **View Diagnostics** controls.
- **GitHub Releases Live Self-Updater:** `Downloads & Updates` now checks official `Subho-XD/Project-Luma` GitHub Releases, validates `SHA256SUMS.txt` / API `sha256` digests, streams updates with redirect-host allowlisting and Zip-Slip checks, and swaps binaries atomically via out-of-process `ProjectLuma.Updater.exe` (with `--health-check` rollback protection and settings/profile preservation).
- **Retained v3.3.0 Fixes:** Persistent per-game FSR quality presets (`[FSR]` + `[QualityOverrides]`), truthful Gameplay Smoothness (`Already available in this game` + RX 6000 DLSS-FG blocking), Windows Tray + Startup Run sync, and non-blocking `Download Official` UI.
"@
[System.IO.File]::WriteAllText((Join-Path $ReleaseDir "RELEASE_NOTES.md"), $NotesContent, [System.Text.Encoding]::UTF8)

Write-Host "==> 11. Generating SHA256SUMS.txt"
$ShaPath = Join-Path $ReleaseDir "SHA256SUMS.txt"
$CheckFiles = Get-ChildItem $ReleaseDir -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | Sort-Object Name
$Lines = foreach ($f in $CheckFiles) {
    $hash = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($f.Name)"
}
[System.IO.File]::WriteAllLines($ShaPath, $Lines)

Write-Host "==> 12. Verifying SHA256SUMS.txt integrity"
foreach ($line in Get-Content $ShaPath) {
    if ($line -match '^([a-f0-9]{64})\s\s(.+)$') {
        $expected = $Matches[1]
        $file = Join-Path $ReleaseDir $Matches[2]
        if (-not (Test-Path $file)) { throw "Missing release artifact for checksum: $file" }
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "Checksum mismatch on $($Matches[2]): expected $expected, got $actual" }
    }
}

Write-Host "==> 13. Release pipeline completed cleanly: $ReleaseDir"
