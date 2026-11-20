# Project Luma v3.1.0 — Riot Client Game Hubs Visual & Structural Audit

**Primary Product Reference:** [Riot Client Game Hubs](https://www.riotgames.com/en/news/riot-client-game-hubs)  
**Comparison Scope:** `v3.0.0` (`docs/screenshots_v3/`) -> `v3.1.0` (`docs/screenshots_v3_1/`)  
**Visual Identity:** Project Luma AMD-inspired `#101010` obsidian black, `#181818`–`#292929` charcoal surfaces, `#ED1C24` primary crimson, bundled **Google Sans** + **Space Mono**, and the supplied white-ghost-on-crimson emblem.

---

## 1. Structural Principles From Riot Client Game Hubs

The Riot Client Game Hubs model centers each installed game as a dedicated destination rather than a settings spreadsheet:
1. **Compact game-oriented navigation:** Top-level section navigation paired with fast direct switching across installed games.
2. **Game-specific landing page (Hub):** Selecting any title opens a full-width hero showcase dominated by game artwork, clear title/logo identity, status badge, and prominent **Play** action.
3. **Secondary content below the hero:** Enhancement status and compatibility belong concisely below the hero fold rather than competing with the primary game launch action.
4. **Minimal unnecessary controls:** Technical diagnostics and tuning knobs stay out of the main consumer workflow.
5. **Cohesive product presentation:** Unified typography, surfaces, badges, buttons, and icons across main client, library, hub, updates, settings, installer, maintenance utility, uninstaller, and launch overlay.

---

## 2. Before vs. After Visual & UX Comparison

| Surface / Dimension | `v3.0.0` Before State (`docs/screenshots_v3/`) | Identified Shortcoming | `v3.1.0` After State (`docs/screenshots_v3_1/`) |
|---|---|---|---|
| **Sidebar Navigation** (`MainWindow.xaml`) | 4 navigation items over empty vertical negative space (`MyGames_1080p_100pct.png`). | Switching to another game required backing out to `My Games` every time. | Added Riot-style **`YOUR GAMES`** quick-jump rail below navigation with 26 px game thumbnails and status indicators across all views (`MyGames_1080p_100pct.png`). |
| **Game Hub Hero** (`GameDetails_1080p_100pct.png`) | `220 px` header banner; profile cards occupied a separate `Recommended settings` dashboard box below hero. | Felt like an engineering configuration form above 4 boxes rather than a game launcher hub. | Elevated to a `296 px` full-bleed hero banner with translucent utility pills (`← My Games`, `Change Cover`, `Change Hero`), game logo, platform/status pills, inline **`ENHANCED PROFILE`** pill selector, **`Enhance`**, and oversized crimson **`▶ Play`** button (`GameDetails_1080p_100pct.png`). |
| **Enhancement Rows** (`GameDetailsView.xaml`) | 4 separate large cards (`AI Visual Enhancement`, `Sharper Image`, `Smoother Gameplay`, `Improved Ray-Traced Details`) displaying repetitive warnings above toggles. | Card clutter + redundant technical warning text inflated page height. | Consolidated into one clean **`YOUR GAME ENHANCEMENTS`** card with 4 single-row consumer toggles (**AI Visuals**, **Image Quality**, **Gameplay Smoothness**, **Ray-Traced Effects**), concise status badges (`Setup required`, `Included in your profile`, `Ready`, `Not supported`), and paired **`YOUR SYSTEM COMPATIBILITY`** + **`INSTALLATION & RECOVERY`** side column (`GameDetails_1080p_100pct.png`, `GameDetails_720p_100pct.png`). |
| **Downloads & Updates** (`DownloadsAndUpdates_1080p_100pct.png`) | Every row rendered an `Install` button including disabled buttons on `Project Luma`, `DLSS-NR-on-AMD`, `AFMF 2`, and `AMDLSS5`. | Confusing disabled buttons suggested broken actions; `DLSS-NR-on-AMD` previously hid update clarity. | Replaced disabled buttons with genuine actions (`Official Release`, `Verify`, `Adrenalin Guide`, `Download Official`), clear state labels (`Installed`, `Needs update`, `Driver ready`, `Not compatible`), and removed dead action buttons where unsupported or current. |
| **Scrollbars & DPI** (`GameDetails_720p_100pct.png`, `GameDetails_1080p_200pct.png`) | Standard light gray Windows scrollbar clashed against obsidian cards at `720p` and `200%` DPI. | Visual seam broken during scrolling on compact screens. | Added a custom 7 px dark obsidian `ScrollBar` template in `LumaTokens.xaml` matching `#343434` / `#585858` / `#ED1C24`. |
| **Installer & Uninstaller** (`screenshots_v3_1/installer/`) | Functional `#101010` / `#ED1C24` dialogs (`v3.0.0`). | `Uninstaller_Main.png` showed stale `v2.0.0` version banner fallback; external `payload\` required next to installer. | Embedded compressed `payload.zip` into standalone `ProjectLuma-Setup-x64.exe`, updated all stage/maintenance banners cleanly to **`v3.1.0`**, and unified dark typography and status pills across `Installer_Stage1_Welcome.png`, `Installer_Stage2_SystemCheck.png`, `Installer_Stage3_InstallFinish.png`, `Uninstaller_Main.png`, and `Uninstaller_Complete.png`. |

---

## 3. Verified Screenshot Inventory (`docs/screenshots_v3_1/`)

All `v3.1.0` screenshots were captured headlessly via `ProjectLuma.dll --capture-screenshots` and `ProjectLuma-Setup-x64.dll --capture-installer-screenshots` and visually inspected via `read_file`:
- **Resolutions & DPIs Covered:** `1280x720` (`720p`), `1366x768` (`768p`), `1920x1080` (`100%`, `125%`, `150%`, `175%`, `200%` DPI), `2560x1440` (`1440p`), `3840x2160` (`4K / 2160p`).
- **Views Verified:** `MyGames`, `GameDetails` (Game Hub), `GameDetails_AdvancedExpanded`, `AvailableEnhancements`, `AvailableEnhancements_Expanded`, `DownloadsAndUpdates`, `Settings`, `Onboarding_Step1`, `Onboarding_Step3`, `SetupDrawer`, `InGameNotification`, plus `installer/Installer_Stage1..3.png` and `installer/Uninstaller_Main.png` / `Uninstaller_Complete.png`.
