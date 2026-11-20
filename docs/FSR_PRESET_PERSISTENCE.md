# FSR Preset Persistence (`v3.3.0`)

## Root Cause in Earlier Builds

1. Controls in [GameDetailsView.xaml](file:///F:/AI/Project%20Luma/src/ProjectLuma.UI/Views/GameDetailsView.xaml) bound directly to nested fields on `SelectedGame.Profile` (`SelectedGame.Profile.SelectedUpscalingPreset`), bypassing `GameCardViewModel` change notification and SQLite persistence.
2. Re-evaluating capabilities after catalog reloads could overwrite customized preset selections back to profile defaults.
3. `OptiScalerSuperResolutionAdapter` wrote `[Upscalers]` and `[Sharpness]` without mapping all `[FSR]` and `[QualityOverrides]` keys.

## v3.3.0 Implementation

- **Two-Way ViewModel Properties:** [GameCardViewModel.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.UI/ViewModels/GameCardViewModel.cs) exposes editable properties (`SelectedUpscalingPreset`, `SelectedUpscalerEngine`, `SelectedFrameGenEngine`, `ProxyDllName`, `EnableSuperResolution`, `EnableFrameGeneration`, `EnableNeuralRendering`, `EnableRayReconstruction`, `ShowFsr4Watermark`, `EnableHudFix`, `EnableAntiLag2`, `OfflineAntiCheatConfirmed`). Changing a visual setting switches `SelectedPreset` to `Custom`, updates badges, and invokes `PersistGameProfileAsync` to SQLite ([MainViewModel.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.UI/ViewModels/MainViewModel.cs)).
- **Non-Destructive Re-evaluation:** `CapabilityEngine.ReevaluatePreservingUserSettings` updates hardware/dependency availability flags while preserving the user's saved `SelectedPreset`, `SelectedUpscalingPreset`, `SelectedUpscalerEngine`, `SelectedFrameGenEngine`, and toggles.
- **INI Mapping ([OptiScalerAdapters.cs](file:///F:/AI/Project%20Luma/src/ProjectLuma.BackendManager/OptiScalerAdapters.cs)):** Writes `[FSR]` (`Fsr4Update`, `Fsr4ForceEnableInt8`, `Fsr4Preset`: `NativeAA=0`, `UltraQuality/Quality=1`, `Balanced=2`, `Performance=3`, `UltraPerformance=5`, `Fsr4EnableWatermark`), matching `[QualityOverrides]` ratios, and `[ProjectLuma] QualityPreset`.

## Verification Summary

- Verified in `FsrPresetPersistence_SurvivesRestartAndReevaluation_And_WritesOptiScalerIni` ([RepairAndReleaseReadinessTests.cs](file:///F:/AI/Project%20Luma/tests/ProjectLuma.Tests/RepairAndReleaseReadinessTests.cs)) across ViewModel selection, `ReevaluateAllCapabilities`, fresh SQLite repository/ViewModel reload, and `OptiScaler.ini` output (`Balanced` vs `Performance`), as well as non-destructively against `Aniimo` and `Bright Memory: Infinite`.
