# Game Artwork & Border Corner Clipping Fix

## Overview
Resolved the visual corner geometry mismatch between game artwork, placeholder backgrounds, hover highlights, and the red selected-state outline across Project Luma's Home, My Games, Game Details, and sidebar Recent Games surfaces without changing the overall Game Hub design.

---

## Root Cause
1. In WPF, `<Border CornerRadius="..." ClipToBounds="True">` clips child elements strictly to the rectangular axis-aligned bounding box rather than the rounded corner curve.
2. As a result, full-bleed `<Image>` artwork and scrim rectangles extended into the rounded corners behind `BorderBrush`, producing visible corner bleeding and sharp diagonal/rectangular clipping artifacts against the 2 px red selected outline (`#ED1C24`).
3. `GameLibraryView.xaml` portrait cards also lacked a dedicated selected-state outline trigger on a separate overlay layer, and `FilePathToBitmapConverter` keyed cached bitmaps solely on file path and width without tracking file write timestamps after in-place artwork replacement.

---

## Implemented Fix

- **Shared Corner Geometry Clipping (`src/ProjectLuma.UI/Controls/RoundedClip.cs`):**
  - Added attached properties `RoundedClip.CornerRadius` and `RoundedClip.Inset` (default `0.5` device-independent px) backed by `RoundedClip.CreateClipGeometry(width, height, cornerRadius, inset)`.
  - Produces a frozen `RectangleGeometry` whose bounds and corner radii follow exact live `ActualWidth` / `ActualHeight` changes via `Loaded` and `SizeChanged`, preventing artwork pixels from leaking past any rounded edge even at fractional Windows DPI scales (`125%`, `150%`, `175%`).
- **Shared Radius & Focus Token (`src/ProjectLuma.UI/Themes/LumaTokens.xaml`):**
  - Added `RadiusGameCard` (`CornerRadius = 12`, identical to `RadiusCard` and `RadiusThumbnail`) and `LumaCardFocusVisual` (`RadiusX="12" RadiusY="12"`).
- **2-Layer Clipped Content + Overlay Outline Pattern:**
  - Applied consistently across:
    - `src/ProjectLuma.UI/Views/HomeView.xaml` — `HeroBannerContainer` (`RadiusHero = 16`) and `"Resume my games"` 4-column thumbnail cards (`RadiusGameCard = 12`).
    - `src/ProjectLuma.UI/Views/GameLibraryView.xaml` — portrait game cards (`196x296`, `RadiusGameCard = 12`) with hover (`#7A7A7A`) and selected (`AccentOpticalBrush`, `BorderThickness="2"`) outline states.
    - `src/ProjectLuma.UI/Views/GameDetailsView.xaml` — Game Hub hero banner (`RadiusHero = 16`).
    - `src/ProjectLuma.UI/MainWindow.xaml` — sidebar Recent Games 28x28 thumbnails (`RadiusBadge = 6`, inset `0.25`).
  - Layer 1 (`controls:RoundedClip.CornerRadius`) wraps the shared rounded `SurfaceSecondaryBrush` placeholder background, `Stretch="UniformToFill"` centered `Image` (`HighQuality` bitmap scaling), fallback monogram/crest, gradient scrims, and platform pills.
  - Layer 2 (`IsHitTestVisible="False"`, `SnapsToDevicePixels="True"`) renders only the card border/hover/selection outline on top with the exact same `CornerRadius`.
- **In-Place Artwork Replacement Cache Fix (`src/ProjectLuma.UI/Converters/LumaConverters.cs`):**
  - `FilePathToBitmapConverter` now includes `File.GetLastWriteTimeUtc(path).Ticks` in its cache key so replaced artwork files refresh immediately.

---

## Verified States & Visual Checks

1. **Automated Geometry & Cache Test (`PatchV332Tests.Task3_RoundedClipGeometry_And_ArtworkCacheInvalidation_MatchSharedCardCorners`):**
   - Verified frozen `RectangleGeometry` dimensions & corner radii for `196x296` cards (`11.5` inner radius at `0.5` inset), `320x160` thumbnails, `1376x420` hero (`15.5` inner radius), `0x0` safe empty rect, narrow `10x10` clamping, and `FilePathToBitmapConverter` cache refresh on modified file timestamps (**Passed**, 26 ms).
2. **Covered Card States:**
   - **Normal:** Clean `1.5 px` hairline charcoal border aligned to rounded artwork.
   - **Hover:** Smooth `#7A7A7A` border overlay without altering artwork clip.
   - **Selected:** Crisp `2 px` AMD red (`#ED1C24`) inset overlay outline matching exact `12 px` card radius with zero corner bleed.
   - **Missing / Generated Fallback & Loading Artwork:** `SurfaceSecondaryBrush` placeholder and fallback monogram sit inside the identically clipped `RoundedClip` layer.
   - **Artwork Replacement:** Cache key invalidates via write timestamp and preserves `Stretch="UniformToFill"` centering.
3. **Multi-Resolution & DPI Visual Verification:**
   - Verified rendered captures across `1920x1080` (`100%`, `125%`, `150%`, `175%`, `200%`), `1280x720`, `1366x768`, `2560x1440`, and `3840x2160` in `docs/screenshots_v3_3_2/`.
