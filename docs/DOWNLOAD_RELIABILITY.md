# Non-Blocking Downloads & SHA-256 Reliability (`v3.3.0`)

## Root Causes Eliminated

1. **Synchronous Directory/Disk Probing on UI Thread:** `GitHubPackageManager.GetManifestsAsync` and `BackendComponentItem.RefreshStateFromManifestAsync` now execute filesystem enumeration asynchronously off the WPF Dispatcher via `Task.Run` / cached background refresh so catalog reloads never stall the UI thread.
2. **Hung Connect / Redirect / Archive Extraction:**
   - Added a 25-second per-redirect-hop connection/header timeout in `GitHubPackageManager.SendRedirectSafeAsync` ([GitHubPackageManager.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.PackageManager/GitHubPackageManager.cs)) alongside the existing 30-second socket stream read timeout.
   - Added `X-RateLimit-Reset` formatting on HTTP 429/403 rate-limit responses.
   - Added cancellation-token chunked copying (`OpenEntryStream()`) inside `.7z` extraction so cancelling mid-extraction aborts cleanly instead of freezing until an entry completes.
   - Added staging failure rollback restore (`Move` previous directory back if atomic swap fails).
3. **Async Command & Busy-State Ordering ([MainViewModel.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.UI/ViewModels/MainViewModel.cs)):**
   - `ApplyUpdateItemCommand` and `DownloadBackendCommand` use `ConcurrentAsyncRelayCommand<T>` tied directly to `IsBusy` property notification so buttons disable/re-enable synchronously without waiting for `CommandManager.RequerySuggested`.
   - `ReloadCatalogsAsync()` finishes before `IsBusy` clears back to `false`, and `ApplyUpdateStatus` preserves active `UpdateItems` so periodic status refreshes never wipe live download progress.

## Verified Behavior

- Verified in live WPF STA UI automation tests (`UiAutomation_ClickDownloadOfficial_RemainsResponsive_UpdatesProgress_SupportsCancel_And_HandlesInvalidArchive` and `GitHubPackageManager_EnforcesDigestSha256_And_SupportsUpdateCentreUiDownloadAndCancel` in [RedesignAndEdgeCaseTests.cs](file:///F:/AI/Project%20Luma/tests/ProjectLuma.Tests/RedesignAndEdgeCaseTests.cs), plus `DownloadsUi_ApplyUpdateItem_NonBlockingOnStaDispatcher_And_HandlesTimeoutAndCancel` in [RepairAndReleaseReadinessTests.cs](file:///F:/AI/Project%20Luma/tests/ProjectLuma.Tests/RepairAndReleaseReadinessTests.cs)).
