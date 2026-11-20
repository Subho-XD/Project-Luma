# Project Luma — Design Skill Execution (Loop 1)

**Primary Design Skill:** Hallmark `v2.0.0` (`Nutlope/hallmark` — `SKILL.md`, `references/redesign.md`, `references/flow.md` § App screens, `references/layout.md`, `references/craft.md`, `references/imagery.md`, `references/gates.md`)  
**Supplementary Reference:** `design-taste-frontend` (`v2.5.1` local skill — applied solely for equal content edges, image-led composition, consistent spacing rhythm, and screenshot-driven critique; web-only scripts skipped per native WPF scope)  
**Target:** Project Luma Desktop Application (`src/ProjectLuma.UI/`), Installer & Maintenance Utility (`src/ProjectLuma.Setup/`), and Artwork Service (`src/ProjectLuma.GameDiscovery/ArtworkDiscoveryService.cs`)  
**Visual Reference:** User-supplied gaming launcher screenshot (`media_1795178380235_c92681f5.jpg`)

---

## 1. Hallmark Workflow Executed

Following Hallmark `SKILL.md` and `references/redesign.md` (Full depth on an existing desktop application, adapted natively for WPF/XAML rather than forcing CSS-only tooling):

1. **Preflight & Brand Truth:**
   - Locked brand assets preserved: supplied ghost icon (`Luma_Icon.svg` / `Luma_Icon.png`), wordmark `PROJECT LUMA` / `GAMING ENHANCEMENT`, bundled `Google Sans` (headings/UI/buttons/navigation) and `Space Mono` (strictly technical metadata: `gfx1032`, versions, hashes, file paths, timestamps), and the AMD-inspired obsidian + crimson token scale (`#101010`, `#181818`, `#202020`, `#292929`, `#333333`, `#ED1C24`, `#FF343B`, `#F6F6F6`, `#B8B8B8`, `#828282`, `#55C78A`, `#E7B45B`, `#EA6666`).
2. **Existing Interface Audit (`v3.1.0` Screenshots & XAML):**
   - **Floor #1 (Tokens & Arbitrary Lengths):** Margins, paddings, gaps, and radii were hardcoded per view (`24,18,24,20`, `24,20,24,28`, `14px` vs `16px` vs `18px` gaps, `CornerRadius` 4/5/6/8/10/12).
   - **Floor #2a / Branding (No Cyan/Blue on Dark):** `ArtworkDiscoveryService.WriteProceduralGradientPng` rendered blue/cyan fallback gradients (`{24, 96, 138}`) on non-Steam cards without covers.
   - **Floor #4 / Surface Ladder (No Card-Inside-a-Card):** `GameDetailsView` nested a yellow `Optional Setup Required` bordered box inside the `YOUR SYSTEM COMPATIBILITY` card; `EnhancementCentreView` hid the entire component manager inside an `<Expander>`.
   - **Floor #9 / Gates 54 & 67 (No Eyebrows or Tracked-Caps Labels):** Tracked-caps `Space Mono` labels appeared as section eyebrows on every screen (`LIBRARY`, `ENHANCEMENTS`, `MANAGEMENT`, `APPLICATION`, `YOUR GAMES`, `PREFERENCES & GAME FOLDERS`, `YOUR GAME ENHANCEMENTS`, `YOUR SYSTEM COMPATIBILITY`, `SETUP STAGES`, `DESTINATION FOLDER`).
   - **Floor #10 / Gate 63 (No Decorative Dots or Glued Arrows):** `<Ellipse>` status dots sat inside `CapabilityBadge.xaml` and sidebar game rows; middle-dot chains (`·`) joined status metadata; arrows (`←`, `→`) were glued into button text strings.
   - **Floor #12 / #13 & Reference Composition:** Default launch landed on `My Games` with a compressed `214px` hero above tall portrait cards, instead of the supplied reference's dominant panoramic Hero Banner (`~400px` at 1080p) paired with a clean 16:9 horizontal `Resume my games` thumbnail strip below.
   - **Floor #14 (Capped Centered Measure at 1440p/4K):** Pages stretched edge-to-edge on ultra-high-resolution screens without a centered `MaxWidth="1440"` frame.

---

## 2. Reference Screenshot Decomposition (Sole Visual Composition Source)

Analyzing the user-supplied screenshot (`media_1795178380235_c92681f5.jpg`) with Hallmark's composition and proportion framework:

| Region | Reference Screenshot Observation | Project Luma Redesign Adaptation |
|---|---|---|
| **Left Sidebar** | Compact `~220px` width, quiet dark surface, brand header at top, 4–5 clean nav links (active link uses soft elevated fill, no heavy border), quiet lower `My favorites` game quick-list | `224px` `#141414` sidebar: Ghost emblem + `PROJECT LUMA` / `GAMING ENHANCEMENT`, 5 nav links (`Home`, `My Games`, `Enhancement Library`, `Downloads`, `Settings`) with subtle `#222222` active pill and clean `3px` AMD-red left accent bar; quiet `Recent games` list below; compact GPU status footer (`AMD Radeon RX 6600` / `gfx1032`). |
| **Top Utility Bar** | Minimal horizontal links (`General  News  Discover`) + quiet top-right action icon; zero boxed card frame | Subtle inline view/filter links (`Featured`, `Ready to enhance`, `Enhanced`) + quiet `Rescan` and `Add Game` links aligned to exact content edges. |
| **Hero Banner** | The single dominant element (~55% of viewport above fold); rounded `16px` panoramic artwork filling the full main-content width; crisp transparent logo left-aligned over a subtle left-to-right scrim; small recommendation pill + 1–2 line description + bold primary rounded CTA button + quiet info beside it | `400px` hero (`CornerRadius="16"`, responsive on 720p so hero + thumbnails stay balanced); Aniimo landscape artwork (`Stretch="UniformToFill"`), left-biased neutral dark vignette keeping right artwork luminous; transparent Aniimo logo, `Tailored for AMD Radeon RX 6600` readiness pill, 1-line tagline, AMD-red `Play` CTA (`44px` tall, `CornerRadius="10"`) + translucent `Enhance` CTA + quiet `Balanced profile · Configure` link. |
| **Thumbnail Strip** | Directly under Hero: `Resume my games` heading + count badge left, `Explore all` link right; wide 16:9 landscape artwork thumbnails with rounded corners, subtle logo overlay inside art, minimal title + `▶` and `•••` icons + quiet status text beneath image, zero boxed outer cards | `Resume my games` header + count chip + `Explore all` link; 4 equal-width 16:9 landscape thumbnails (`Aniimo`, `Cyberpunk 2077`, `Red Dead Redemption 2`, `Alan Wake 2`) strictly matching Hero's left and right boundaries (`16px` gaps); clicking thumbnail swaps the Hero live; `▶` launches game; `•••` opens `Game Details`. |
| **Right-Hand Social Column** | Present in original screenshot (`Friends`, `Requests`, chat list) | **Explicitly removed.** Main workspace expands cleanly across the full available width inside a centered `MaxWidth="1440"` container. |

---

## 3. Hallmark Pre-Emit Critique (v3.2.0 Target)

`Hallmark · pre-emit critique: P5 H5 E4 S5 R5 V5 · weakest: E (XAML DataTemplate selection border & multi-DPI hero height tuning on 720p need live screenshot validation) · fix: adaptive hero height & verified ObjectReferenceEqualsMultiConverter`

- **P (Philosophy = 5):** Image-led game launcher first, engineering tool second — Open Project Luma, see Aniimo's artwork immediately, Enhance, Play.
- **H (Hierarchy = 5):** Clear visual dominance: Panoramic Hero (~55% height) -> Thumbnail Strip -> Quiet Enhancement Strip -> Subtle Navigation.
- **E (Execution = 4):** Unified 4pt spacing tokens across App, Setup, and Uninstaller, zero eyebrows, zero decorative dots.
- **S (Specificity = 5):** Every badge, status, and hardware label ties directly to real AMD RX 6600 (`gfx1032`) / OptiScaler / DLSS-NR-on-AMD state.
- **R (Restraint = 5):** Removed Riot branding, removed right-hand social panel, removed nested card borders, removed tracked-caps eyebrows, kept AMD red `< 5%` of viewport as signal only.
- **V (Variety = 5):** Distinct horizontal composition for `Home` vs portrait cover library (`My Games`), categorized component catalog (`Enhancement Library`), live progress list (`Downloads`), and balanced 2x2 `Settings`.

---

## 4. Planned Changes Summary

1. Introduce semantic 4-point spacing & surface tokens in `LumaTokens.xaml` and `SetupTokens.xaml`.
2. Build `HomeView.xaml` around Aniimo first and validate with multi-resolution screenshot critique (`docs/ANIIMO_GAME_HUB_REVIEW.md`).
3. Propagate tokens and Floor rules across `MainWindow`, `GameLibraryView`, `GameDetailsView`, `EnhancementCentreView` (`Enhancement Library`), `UpdateCentreView` (`Downloads`), `SettingsView`, `LumaLaunchOverlayWindow`, `SetupWindow`, and `UninstallWindow`.
4. Replace blue/cyan procedural fallback artwork with warm obsidian/crimson-graphite tones and ensure real artwork resolution for Aniimo, Cyberpunk 2077, Red Dead Redemption 2, and Alan Wake 2.
