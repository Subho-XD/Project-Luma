# Project Luma — Architecture Audit (`v3.0.0`)

## 1. Layered Solution Topology
- **Domain & Contracts (`ProjectLuma.Core`):** Zero UI/OS coupling; defines enums (`GpuArchitecture`, `CapabilityState`, `ComponentInstallState`, `GameLaunchPhase`), immutable/bindable domain models, and `IServices` interfaces (`IGpuDetector`, `ICapabilityEngine`, `IGameLibraryScanner`, `ArtworkDiscoveryService` -> `IArtworkDiscoveryService`, `IUserDllVault`, `IPackageManager`, `IIntelligentComponentResolver`, `IGameInstaller`, `IGameLaunchService`).
- **Platform & Detection (`ProjectLuma.GpuDetection`, `ProjectLuma.GameDiscovery`):**
  - Native COM DXGI `IDXGIFactory1::EnumAdapters1` enumeration prevents 32-bit WMI 4 GB truncation bugs; multi-GPU prioritization ranks discrete AMD GPUs first.
  - Display registry & WMI enrich Adrenalin version strings without hardcoding local hardware specs.
  - Game scanners parse Valve VDF/ACF, Epic JSON `.item`, GOG/Ubisoft/EA/Battle.net manifests, and Xbox `appxmanifest.xml` with path-traversal guards and non-destructive PE32+ IAT analysis.
- **Package, Component & Game Install Integrity (`ProjectLuma.PackageManager`, `ProjectLuma.BackendManager`, `ProjectLuma.GameInstaller`):**
  - Network transfers are restricted to allowlisted HTTPS GitHub hosts, use streaming chunked I/O with per-backend `SemaphoreSlim` locks, single-pass `.7z` extraction, SHA-256 verification, and rollback archives.
  - Per-game deployments write transactional `.luma_backup/<session>/manifest.json` snapshots before touching any file, enforce anti-cheat & process-running guards, and restore original files byte-exact during uninstall/recovery.
- **Presentation & Setup (`ProjectLuma.UI`, `ProjectLuma.App`, `ProjectLuma.Setup`, `ProjectLuma.Updater`):**
  - WPF hardware-accelerated rendering with row virtualization (`VirtualizingPanel.IsVirtualizing="True"`) for 1,000+ games and embedded Google Sans + Space Mono font resources + exact vector ghost branding.
