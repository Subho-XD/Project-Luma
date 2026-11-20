# Project Luma v3.1.0 — Release Notes

## Highlights
- **Riot-Client-Inspired Game Hubs:** Redesigned My Games and dedicated per-game hubs (Play + Enhance hero actions, inline profile pills, clean 4-row YOUR GAME ENHANCEMENTS, compact YOUR SYSTEM COMPATIBILITY & INSTALLATION & RECOVERY panels, and sidebar YOUR GAMES quick-jump rail).
- **Autonomous Neural Rendering Setup & Downloads Watcher:** Automatically scans registered game folders and %USERPROFILE%\Downloads via debounced FileSystemWatcher for valid 
vngx_dlss*.dll files (>= 310.9.16.0 CC model support) and user-downloaded DLSS-NR-on-AMD releases.
- **Reliable Download & Archive Pipeline:** Fixed stale persisted "Installed" state drift, isolated API vs binary HTTP Accept headers, safe HTTP 416 clean-retry handling, cross-volume archive fallback, live throttled extraction progress (91%–98%), and off-UI-thread SHA-256 verification.
- **Intelligent Component Management:** Genuine actions on every visible button (Verify, Download Official, Update, Official Release, Adrenalin Guide) with clear status badges and zero disabled fake install buttons on Project Luma or unsupported hardware.
- **Standalone Self-Extracting Installer (ProjectLuma-Setup-x64.exe):** Clean versioned deliverable under elease/v3.1.0/ with embedded compressed payload, SHA-256 checksum manifest, and third-party license notices.