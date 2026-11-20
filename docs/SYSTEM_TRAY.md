# Windows System Tray Integration (`v3.3.0`)

## Implemented Architecture

- **Testable State Machine:** [TrayLifecycleController.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.UI/ViewModels/TrayLifecycleController.cs) coordinates minimize-on-close (`HandleWindowClosing`), restore (`RequestRestore`), explicit exit (`RequestExplicitExit`), preference synchronization (`SyncPreference`), and Explorer taskbar restart recovery (`HandleTaskbarCreated`).
- **Native Tray Host:** [WindowsSystemTrayService.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.App/WindowsSystemTrayService.cs) uses `System.Windows.Forms.NotifyIcon` (scoped via `global using` removal in `ProjectLuma.App.csproj` so WPF types remain unambiguous), loads `project-luma.ico`, provides a dark-compatible native `ContextMenuStrip` (`Open Project Luma`, `Exit Project Luma`), restores on left-click/double-click, and hooks `HwndSource` `WndProc` for `RegisterWindowMessage("TaskbarCreated")`.
- **Application Lifecycle:** [App.xaml.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.App/App.xaml.cs) configures `ShutdownMode.OnExplicitShutdown` in GUI mode so closing the main window when `Minimize to tray` is enabled hides the window rather than exiting the process, supports `--minimized`, and cleanly disposes the `NotifyIcon` on explicit exit or when closing with `Minimize to tray` disabled.

## Verified Behavior

- Unit-verified in `TrayLifecycleController_HandlesHideRestoreExplorerRestart_And_ExplicitExit` inside [RepairAndReleaseReadinessTests.cs](file:///F:/AI/Project%20Luma/tests/ProjectLuma.Tests/RepairAndReleaseReadinessTests.cs).
- Settings UI captured in [Settings_StartupAndTray_1080p_100pct.png](file:///F:/AI/Project%20Luma/docs/screenshots_v3_3/Settings_StartupAndTray_1080p_100pct.png).
