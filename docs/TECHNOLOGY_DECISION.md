# Project Luma v3.0.0 — Technology Stack Decision

## Evaluated Options
1. **Option A: Rust + Tauri 2 (React + WebView2):** Strong Rust backend safety, but UI rendering executes inside Edge WebView2 Chromium multi-process containers.
2. **Option B: C++ + Qt Quick 6:** Direct3D-accelerated QML UI, but requires a multi-gigabyte Qt SDK toolchain, QML/C++ marshaling overhead, and rewriting all 12 verified `.NET 10` libraries (`DxgiGpuEnumerator`, `PeAnalyzer`, `TransactionalGameInstaller`, `SqliteCompatibilityRepository`, `ArtworkDiscoveryService`).
3. **Option C: Retain & Upgrade `.NET 10 / WPF`:** Direct reuse of the mature backend, hardware-accelerated Direct3D 9 / DWM WPF composition, native P/Invoke DXGI/Win32 access, and bundled vector/font rendering.

## Measured Prototype Benchmark Data (Verified on Host Machine)
*(From local `benchmarks/WpfBenchmark` & `benchmarks/WebViewPrototype` runs documented on this RX 6600 / Ryzen 5 5500 Windows 11 Pro host):*
- **.NET 10 / WPF Prototype (`1,000` virtualized portrait game cards):**
  - Cold startup (internal): **`468.94 ms`** (process wall clock `1,094 ms`)
  - Memory footprint (`idle` -> `1,000` loaded virtualized cards): **`131.44 MB` -> `141.01 MB`** (`+9.57 MB` delta)
  - `1,000`-game evaluation/filter/sort/row-chunking pipeline: **`29 ms`**
  - Search & UI filter responsiveness: **`1.28 ms`**
- **Chromium / WebView2 Host Baseline:**
  - Spawned `17` child processes totaling **`930.02 MB`** working set (`6.6x` higher RAM footprint than WPF at `1,000` games).

## Final Architectural Decision
**Selected: Option C — Upgrade the Existing `.NET 10 / WPF` Application.**
It meets the `<= 3s` cold startup, `< 250 MB` idle memory, `< 100 ms` responsiveness, and `1,000+` game virtualization targets without rewriting verified filesystem rollback, PE inspection, or DXGI hardware detection code.
