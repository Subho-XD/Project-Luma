# Standalone GUI Installer Verification Report (`v3.3.0`)

**Executable Under Test:** [ProjectLuma-Setup-x64.exe](file:///F:/AI/Project%20Luma/release/v3.3.0/ProjectLuma-Setup-x64.exe)  
**Test Method:** Live Windows UI Automation (`System.Windows.Automation`) interacting with compiled self-contained GUI installer into isolated destination directory `%TEMP%\ProjectLumaGuiTest_v330` (Desktop and Start Menu shortcuts unchecked during isolated test).

---

## Observed Execution Steps

1. **Window Launch:** `Project Luma Setup — v3.3.0` rendered and accepted automation focus immediately (`0` crash/hang).
2. **Stage 1 -> Stage 2 (`SetupNextBtn`):** Navigated cleanly to `System Check` showing RX 6600 (`gfx1032`) compatibility items.
3. **Stage 2 -> Stage 3 (`SetupNextBtn`):** Navigated cleanly to `Install Destination`; set `SetupInstallDirBox` text to `C:\Users\ghoru\AppData\Local\Temp\ProjectLumaGuiTest_v330`.
4. **Embedded Payload Extraction & Install:** Clicked `SetupNextBtn` (`Install Now`); observed completion banner:
   `Project Luma v3.3.0 installed to C:\Users\ghoru\AppData\Local\Temp\ProjectLumaGuiTest_v330`
5. **Completion & Exit:** Unchecked `SetupLaunchAfterFinishCheck` and clicked `Finish` (`SetupNextBtn`); process exited with code `0`.
6. **Binary & Runtime Verification:**
   - `FileVersionInfo.GetVersionInfo("ProjectLuma.exe").FileVersion` = `3.3.0.0`
   - `ProjectLuma.exe --health-check` exited with code `0`
   - `ProjectLuma-Uninstall.exe` created in target directory.
7. **Host Production Upgrade Check:** Upgraded `%LocalAppData%\Programs\ProjectLuma` cleanly via `--silent` switch; verified `3.3.0.0` binary version and `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProjectLuma` registration.
