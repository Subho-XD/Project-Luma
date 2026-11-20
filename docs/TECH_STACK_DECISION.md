# Project Luma — Technology Stack Benchmark & Architectural Decision

**Evaluation Date:** 2026-11-19  
**Test System:** AMD Ryzen 5 5500 (6C/12T), 32 GB DDR4, AMD Radeon RX 6600 (8 GB), Windows 11 Pro Build `26300`, `.NET 10.0.15` (`SDK 10.0.404`), Microsoft Edge / WebView2 `157.0.4341.60`, Node.js `v26.13.0`.

---

## 1. Candidate Architectures Evaluated

1. **Option A — Rust + Tauri 2 (WebView2 + HTML5/React Frontend)**
   - Architecture: Rust backend IPC over `tauri-ipc` driving an embedded Microsoft Edge WebView2 (`msedgewebview2.exe`) Chromium renderer.
   - Strengths: Web CSS ecosystem (`flex`/`grid`, backdrop blur, transitions), small ~10–15 MB installer when relying on system WebView2, low backend binary size.
   - Trade-offs: Multi-process Chromium WebView2 memory overhead, IPC serialization boundary for live DXGI/PE/filesystem streams, `rustc`/`cargo` toolchain + MSVC build tools absent on host, and full rewrite cost for 11 verified `.NET 10` backend/installer/updater modules.

2. **Option B — C++ + Qt Quick 6 (QML Direct3D 11/12 Scene Graph)**
   - Architecture: Native C++ core with QML hardware-accelerated scene graph.
   - Strengths: Direct3D RHI scene graph, smooth 60–144 FPS animations, true native memory layout.
   - Trade-offs: Qt 6 SDK + CMake + MSVC compiler absent on host (requiring multi-GB toolchain installation on `C:`), LGPL-3.0 / Commercial licensing constraints for static linking or closed distribution, slower UI iteration speed, and complete rewrite of existing SQLite/GitHub/WPF/PE modules.

3. **Option C — Existing `.NET 10 / WPF` (Direct3D Hardware-Accelerated Pipeline + UI Virtualization)**
   - Architecture: Single-process native Windows `.NET 10` application utilizing WPF's Direct3D 9/11 hardware-accelerated MIL composition thread, `VirtualizingStackPanel` row recycling, and `BitmapImage` `DecodePixelWidth` async thumbnail caching.
   - Strengths: Zero rewrite of verified DXGI COM interop, Registry/WMI probes, 7-launcher scanners, PE32+ analyzer, SQLite compatibility DB, transactional game installer/rollback, and atomic self-updater.

---

## 2. Measured Prototype Benchmark Results

Equivalent 1,000-game library prototypes were implemented (`benchmarks/WpfBenchmark/` and `benchmarks/WebViewPrototype/`) using real local Steam portrait cover artwork (`library_capsule.jpg`), search filtering, animated transitions, and 1,000 card entries on the target RX 6600 system.

| Metric | Option C: `.NET 10 / WPF` (Measured) | Option A: Chromium / WebView2 Prototype (Measured) | Option B: C++ / Qt Quick 6 (Architectural Assessment) |
|---|---|---|---|
| **Cold Startup (to usable UI)** | **`468.94 ms`** (process launch-to-exit wall time: `1,094 ms`) | `> 1,500 – 6,433 ms` (cold isolated `--user-data-dir` profile warmup + multi-process init) | ~`200 – 450 ms` (native C++ + QML engine init) |
| **Idle Working Set Memory** | **`131.44 MB`** (1 process) | `~350 – 930.02 MB` across `17` Edge/WebView2 processes | ~`70 – 120 MB` (estimated single process) |
| **1,000-Game Library Working Set** | **`141.01 MB`** (`+9.57 MB` delta with `VirtualizingStackPanel` + `DecodePixelWidth=200` cache) | `930.02 MB` combined working set (unvirtualized 1,000 DOM nodes + image decoders) | ~`95 – 140 MB` (with QML `GridView` delegate recycling) |
| **1,000-Game Layout Realization** | **`33.94 ms`** (200 virtualized rows × 5 cards) | `~10–40 ms` DOM fragment append (excluding async image decode & paint) | ~`15–30 ms` |
| **1,000-Game Search / Filter** | **`1.28 ms`** (C# LINQ `OrdinalIgnoreCase`) | `0.14 ms` JS array filter (Node V8) | `< 1 ms` (C++ QSortFilterProxyModel) |
| **Artwork Decode (`200px` Cover)** | **`26.06 ms`** (cached frozen `BitmapImage`) | Browser-managed async decode | Fast native QImage cache |
| **UI Animations & Transitions** | Hardware-accelerated composition thread (`DoubleAnimation` / `CubicEase` at monitor refresh) | Hardware-accelerated CSS transforms | Hardware-accelerated D3D11/12 RHI Scene Graph |
| **Self-Contained Package Footprint** | `144.78 MB` uncompressed (`~55 MB` compressed installer) with zero external runtime required | ~`15 MB` + requires WebView2 Runtime (`~150 MB` pre-installed on Win 11) | ~`45 – 75 MB`Qt6 DLL bundle |
| **Existing Backend Compatibility** | **100% Native** (reuses all 11 verified C# modules & 12 passing tests directly) | Requires complete FFI/IPC rewrite or sidecar bridge | Requires complete C++ rewrite of all 11 modules |
| **Host Toolchain Readiness** | **Ready** (`.NET 10.0.404` already installed) | Missing `rustc`/`cargo` & MSVC linker | Missing Qt 6 SDK, CMake & MSVC |

---

## 3. Final Architectural Decision

**Decision: Retain and Upgrade `.NET 10 / WPF` (Option C).**

### Justification
1. **Measured Performance Meets All Targets:** `.NET 10 / WPF` achieves sub-500 ms cold startup (`468.94 ms`), `141 MB` single-process memory footprint with 1,000 virtualized game covers (`+9.57 MB` over idle), `33.94 ms` layout realization, and `1.28 ms` search filtering — outperforming multi-process WebView2 (`930 MB` across 17 processes) in memory efficiency and startup speed.
2. **UI Modernization Is a Design Problem, Not a Language Bottleneck:** The previous application felt like a developer utility because of layout structure, missing game artwork, exposed DLL terminology, and lack of progressive disclosure — **not** because of WPF. WPF natively supports `ImageBrush` hero banners with gradient opacity masks, `Border` rounded cover cards, `VirtualizingStackPanel` 1,000-game grids, `DoubleAnimation` smooth transitions, and `RenderOptions.BitmapScalingMode="HighQuality"`.
3. **Zero Risk to Verified Hardware & Safety Engines:** Retaining `.NET 10` preserves our proven DXGI COM GPU detector, 7-store game scanner, PE32+ analyzer, SQLite compatibility engine, transactional SHA-256 backup/rollback engine, and atomic self-updater (`ProjectLuma.Updater.exe`), freeing all development effort for genuine user-experience, artwork discovery, intelligent dependency resolution, and edge-case reliability.
