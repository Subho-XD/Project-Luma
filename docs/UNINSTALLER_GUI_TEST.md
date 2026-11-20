# Maintenance & GUI Uninstaller Verification Report (`v3.3.0`)

**Executable Under Test:** `ProjectLuma-Uninstall.exe` (deployed by `release\v3.3.0\ProjectLuma-Setup-x64.exe` into `%TEMP%\ProjectLumaGuiTest_v330\ProjectLuma-Uninstall.exe`)  
**Test Method:** Live Windows UI Automation (`System.Windows.Automation` + Win32 modal button verification) against the running uninstaller GUI, protecting existing user settings/backups by unchecking `UninstallRestoreGamesCheck` and `UninstallRemoveCacheCheck` while keeping `UninstallPreserveBackupsCheck` checked.

---

## Observed Execution Steps

1. **Window Launch:** Opened `Project Luma Maintenance & Uninstaller — v3.3.0` (`UninstallWindow`).
2. **Confirmation Cancel Path (`No`):**
   - Clicked `UninstallRunBtn` (`Uninstall Project Luma`).
   - Observed modal confirmation dialog `"Confirm Project Luma Uninstall"` with `"No"` button focused/default.
   - Clicked `No` -> uninstall aborted safely; `UninstallRunBtn.IsEnabled` remained `True` and `ProjectLuma.exe` remained intact on disk.
3. **Confirmation Accept Path (`Yes`):**
   - Clicked `UninstallRunBtn` again -> clicked `Yes` on `"Confirm Project Luma Uninstall"`.
   - Observed completion banner: `Project Luma has been removed cleanly.`
   - Clicked `UninstallCloseBtn` (`Close`); process exited cleanly with code `0`.
4. **Cleanup Verification:**
   - Verified `ProjectLuma.exe` was completely removed (`Post-uninstall ProjectLuma.exe still exists: False`).
