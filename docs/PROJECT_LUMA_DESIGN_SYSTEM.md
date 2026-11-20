# Project Luma — Redesigned Visual Design System

**Design Influences:**
- **Interface Design (`Dammyjay93/interface-design`):** Intent-driven visual hierarchy, consistent token scale, quiet structural separation, states across every interaction, zero visual slop.
- **Taste Skill (`Leonxlnx/taste-skill`):** Product identity rooted in the subject's world, restrained accent usage, honest status communication, verified by real rendered screenshot review across DPI scales.
- **Gaming Library References:**
  - **Playnite Desktop Mode:** Vertical portrait cover grid (`2:3` aspect ratio), game details hero banner behind title/logo, clean filter bar without dense developer tables.
  - **Steam Library:** Wide atmospheric hero header with gradient vignette, transparent game logo overlay, prominent primary action CTA (`Apply Recommended Enhancements`), and quick status clarity.
  - **Heroic Games Launcher:** Approachable onboarding/setup steps, clear status pills on covers, progressive disclosure separating simple actions from advanced configuration.

---

## 1. Visual Direction: "Theater Obsidian & Optical Cyan"

- **Who Uses It:** A regular AMD Radeon PC gamer who wants games to look sharper and run smoother without learning what `DXGI`, `HIP`, `NVNGX`, or `RCAS` mean.
- **Feel:** Opening a sleek, modern gaming library — rich vertical cover art and atmospheric hero banners take center stage, while dark obsidian surfaces frame the artwork silently.

### Rejected Defaults
1. **Rejected:** 4-letter developer badges (`NR`, `SR`, `FG`, `RR`) and `.exe` names on library cards -> **Adopted:** Clean `2:3` portrait cover art occupying ~82% of the card, subtle store tag (`Steam`, `Epic`, etc.), and one plain-English status badge (`Ready to Enhance`, `Enhanced`, `Setup Required`, `Limited Support`, `Not Supported`).
2. **Rejected:** Opening on a GPU telemetry/hardware table -> **Adopted:** Opening directly on **`My Games`** with a compact GPU + library summary pill in the sidebar footer.
3. **Rejected:** Bordered rectangular cards around every tiny section -> **Adopted:** Seamless surface hierarchy (`#080A0F` canvas -> `#0D1119` elevated surfaces -> `#131824` hover/active) and soft gradient hero masks.
4. **Rejected:** Static persistent banners for routine successes -> **Adopted:** Auto-dismissing 3.5-second toast pills for routine events, inline cancellable progress drawer for active setups/scans, and persistent alerts only when user action is required.

---

## 2. Color & Status Token System

| Token | Hex | Purpose |
|---|---|---|
| `SurfaceCanvasBrush` | `#080A0F` | Deep theater background across app shell and library |
| `SurfaceSidebarBrush` | `#0A0D14` | Navigation rail with `1px` `#171C28` hairline border |
| `SurfaceCardBrush` | `#0E131D` | Game cards, profile tiles, option rows |
| `SurfaceElevatedBrush` | `#141B29` | Hover states, dropdowns, modal surfaces |
| `SurfaceInputBrush` | `#06080C` | Search bar & inset inputs |
| `BorderHairlineBrush` | `#192030` | Subtle structural borders |
| `AccentOpticalBrush` | `#38BDF8` | Primary gaming CTA (`Apply Recommended Enhancements`, active card focus) |
| `FriendlyEnhancedBrush` | `#38BDF8` (`#0B2638` bg) | `Enhanced` — Game is already enhanced and verified |
| `FriendlyReadyBrush` | `#10B981` (`#07291F` bg) | `Ready to Enhance` — Ready for 1-click setup |
| `FriendlySetupBrush` | `#F59E0B` (`#2B1E08` bg) | `Setup Required` — Optional or required component needs one step |
| `FriendlyLimitedBrush` | `#A78BFA` (`#1E1634` bg) | `Limited Support` — Some enhancements available with caveats |
| `FriendlyUnsupportedBrush` | `#F43F5E` (`#290F18` bg) | `Not Supported` — Anti-cheat protected or incompatible binary |

---

## 3. Navigation Architecture (4 Primary Destinations)

```text
PROJECT LUMA

LIBRARY
  My Games                    (Default home view: Hero showcase + virtualized cover grid)

ENHANCEMENTS
  Available Enhancements      (Plain-English overview of what your AMD GPU supports + component resolver)

MANAGEMENT
  Downloads & Updates         (Automatic background updates, staged releases, simple progress)

APPLICATION
  Settings                    (Library folders, update automation, artwork cache, diagnostics)
```

- **Game Details** is entered contextually by clicking any game card or Hero `Enhance & Configure` CTA, with a clear **`← Back to My Games`** breadcrumb.

---

## 4. Typography & Layout Proportions
- **Display / Titles:** `Space Grotesk, Segoe UI Variable Display, Segoe UI` (`26px` Hero title, `20px` Section heading, `15px` Card/Option title).
- **UI Body:** `Manrope, Segoe UI Variable Text, Segoe UI` (`13.5px` body, `12px` metadata, `11px` status pills).
- **Monospace (Advanced Drawer & Logs only):** `Cascadia Mono, Consolas`.
- **Portrait Cover Card Size:** `196 × 286` cover image area (`~2:3` Steam/Playnite standard capsule ratio) + `58px` bottom title/status footer (`196 × 344` total card footprint) rendered inside a `VirtualizingStackPanel` row-grouped virtualizer capable of 1,000+ games smoothly.
