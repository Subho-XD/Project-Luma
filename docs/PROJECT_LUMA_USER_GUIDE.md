# Project Luma v3.3.0 — User Guide

![Project Luma Home Hub](screenshots_v3_3/Home_1080p_100pct.png)

## 1. Overview & Supported Hardware

Project Luma is a Windows 10/11 (x64) desktop enhancement launcher built for **AMD Radeon RDNA 1, RDNA 2, RDNA 3, and RDNA 4** graphics cards.

- **Verified Baseline Hardware:** `AMD Radeon RX 6600` (`VEN_1002&DEV_73FF`, `RDNA2` / `gfx1032`, `7.96 GB` VRAM, WDDM `32.0.21045.29003` / Adrenalin `26.11.2`, Windows 11 Pro x64).
- **Safe Non-Destructive Workflow:** Original game files are backed up with SHA-256 manifests before deployment and can be verified or restored cleanly at any time.
- **Strict Account Protection:** Games with active kernel anti-cheat (e.g., `PUBG`, `VALORANT`) remain untouched unless an official offline mode is supported and explicitly confirmed.

---

## 2. Installing, Upgrading, and Uninstalling

1. **Interactive Installer:** Run `ProjectLuma-Setup-x64.exe` from `release\v3.3.0\` and follow **Welcome -> System Check -> Install & Finish**.
2. **Upgrading:** Running the v3.3.0 setup over an older installation preserves your SQLite settings/profiles, replaces runtime binaries cleanly, and updates your Windows startup Run command if enabled.
3. **Uninstalling / Repairing:** Open **Windows Settings -> Apps -> Installed apps -> Project Luma**, or run `ProjectLuma-Uninstall.exe`. Click **Repair** to verify and restore application files, or **Uninstall** (which prompts for explicit `Yes/No` confirmation) to optionally restore backed-up game files, clean preferences, and remove startup/Installed Apps entries.

---

## 3. Enhancing a Game

1. Select a game on **Home** or **My Games** and open its **Game Hub**.
2. Choose a one-click preset (**Balanced**, **Visual Quality**, **Smoother Gameplay**, or **Custom**), or adjust individual toggles:
   - **Image Quality (`OptiScaler`):** Supports AMD FSR 4.1.2 INT8 on RDNA 2, FSR 3.1.2, and Intel XeSS. Your selected Resolution Quality Preset (`Quality`, `Balanced`, `Performance`, `UltraPerformance`, `NativeAA`) persists automatically across restarts and writes directly to `OptiScaler.ini`.
   - **Gameplay Smoothness (Frame Generation):**
     - If a game already includes native Frame Generation, Project Luma sets the badge to **Already available in this game**, disables conflicting OptiFG injection, and instructs you how to enable native FG in-game.
     - If native FG is absent on a 64-bit DX12 title, toggle **Gameplay Smoothness** on to configure `OptiFG DX12 + HUDfix` or `AMD FSR Frame Generation 4.0.2`.
     - **Note for RX 6000 (RDNA 2):** `DLSS Frame Generation` via `DLSS-NR-on-AMD v0.9.0` is unavailable on RX 6000 GPUs and cannot be enabled there.
     - **DX11 / Vulkan titles:** Project Luma directs you to enable **AMD Adrenalin AFMF 2** in AMD Software.
   - **AI Visuals (Neural Rendering):** Experimental path. Click **Find Required Components** beneath `AI Visuals` to scan your GPU, game, NeuralHub installation, game folders, Downloads folder, and HIP runtime status in one 7-stage checklist. See [NEURAL_RENDERING_DLL_IMPORT_GUIDE.md](NEURAL_RENDERING_DLL_IMPORT_GUIDE.md).
3. Click **Enhance** to apply components safely with automatic backup creation, then click **Play**.
4. Expand **Advanced settings (for enthusiasts)** at the bottom of a Game Hub to pick a custom proxy DLL name, upscaler engine, watermark verification toggle, or HUD fix toggle.

---

## 4. Downloads, System Tray & Windows Startup

- **Downloads Page & Enhancement Library:** Click **Download Official** for open-source packages like `OptiScaler` to stream with live progress, SHA-256 verification, and non-blocking cancellation.
- **Minimize to Tray (`Settings -> General`):** Enabling **Minimize to tray when closing main window** keeps Project Luma running silently in the Windows notification area on close; double-click or right-click **Open Project Luma** to restore, or **Exit Project Luma** to quit.
- **Start Project Luma with Windows (`Settings -> General`):** Writes and verifies `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ProjectLuma` without administrator prompts.
