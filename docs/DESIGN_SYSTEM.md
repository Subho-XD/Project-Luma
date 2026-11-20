# Project Luma — Design System & Visual Direction

## 1. Domain Exploration & Design Intent
- **Domain Concepts:** Optical instruments, spectral analysis, GPU compute pipelines, shader compilation telemetry, light scattering, calibrated laboratory workbenches.
- **Who Opens This:** An AMD Radeon enthusiast / graphics tuner inspecting exact hardware capabilities (`RX 6600 · RDNA 2 · 8 GB`), validating prerequisites (`HIP 7.2`, user DLL hashes), and deploying safe rendering upgrades without guesswork.
- **Feel:** An **Optical & Neural Rendering Laboratory** — obsidian-graphite surfaces with razor-sharp 1px hairline structure, tabular monospace telemetry, and restrained spectral color reserved strictly for live capability states, data signals, and primary actions.

### Rejected Defaults (Anti-Slop Decisions)
1. **Rejected:** Generic gaming launcher neon purple-blue gradients -> **Adopted:** Single cool-obsidian surface scale (`#0A0B0E` -> `#0F1117` -> `#14171F`) with optical cyan (`#38BDF8`) reserved strictly for primary actions and optical pipeline focus.
2. **Rejected:** Floating glossy cards with heavy drop shadows -> **Adopted:** Flat surfaces separated by 1px hairline borders (`#1B1F2A` / `7%` white opacity) and whisper-quiet lightness steps.
3. **Rejected:** Uniform KPI boxes with icons in tinted circles -> **Adopted:** Type-led telemetry strips with tabular numerals and the **4-Stage Rendering Pipeline Matrix Bar** (`[NR] -> [SR] -> [FG] -> [RR]`) as the signature product element.
4. **Rejected:** Emoji icons -> **Adopted:** Crisp 16px vector `StreamGeometry` icons (`StrokeThickness=1.5`) unified in `Icons.xaml`.

---

## 2. Color Tokens (60 / 30 / 10 Rule)

| Token Name | Hex | Role |
|---|---|---|
| `SurfaceCanvasBrush` | `#0A0B0E` | Main workspace & sidebar ground (60% dominant obsidian) |
| `SurfaceCardBrush` | `#0F1117` | Primary structured panels & game cards (+1 step elevation) |
| `SurfaceElevatedBrush` | `#14171F` | Hover states, active nav pill, popovers (+2 step elevation) |
| `SurfaceInputBrush` | `#07080A` | Inset text inputs, search bars, terminal/log panels |
| `BorderHairlineBrush` | `#1B1F2A` | Primary 1px structural boundaries & table dividers |
| `BorderSubtleBrush` | `#141720` | Internal row separators & secondary gridlines |
| `BorderEmphasisBrush` | `#2A3142` | Active control outline & interactive hover borders |
| `TextPrimaryBrush` | `#F1F5F9` | Primary titles, active values, hero GPU telemetry |
| `TextSecondaryBrush` | `#94A3B8` | Field labels, technical descriptions, secondary copy |
| `TextTertiaryBrush` | `#64748B` | Captions, table headers, timestamps |
| `TextMutedBrush` | `#475569` | Disabled controls, unavailable capability explanations |
| `AccentOpticalBrush` | `#38BDF8` | Primary action CTA, active pipeline focus, selection indicator |
| `AccentOpticalSubtleBrush` | `#0C2536` | Subtle background for primary focus badges |
| `StatusSupportedBrush` | `#10B981` | `Supported` capability & verified hash state |
| `StatusSupportedBgBrush` | `#07261E` | Subtle fill for `Supported` badges |
| `StatusExperimentalBrush` | `#F59E0B` | `Experimental` / `SupportedWithLimitations` / Drift warning |
| `StatusExperimentalBgBrush` | `#291D08` | Subtle fill for `Experimental` badges |
| `StatusDependencyBrush` | `#A78BFA` | `RequiresDependency` / `RequiresDriver` / `RequiresGameIntegration` |
| `StatusDependencyBgBrush` | `#1E1633` | Subtle fill for prerequisite-gated badges |
| `StatusUnsupportedBrush` | `#F43F5E` | `Unsupported` / Anti-cheat blocked / Verification failure |
| `StatusUnsupportedBgBrush` | `#2A0E17` | Subtle fill for `Unsupported` / blocked badges |

---

## 3. Typography & Scale (Ratio ~1.25, Tabular Numerals)
- **Display / Headings (`FontDisplay`):** `Space Grotesk`, fallback `Segoe UI Variable Display`, SemiBold/Bold, tight tracking (`-0.02em` on Display/H1).
- **UI & Prose (`FontUi`):** `Manrope`, fallback `Segoe UI Variable Text`, Regular/Medium/SemiBold.
- **Technical Telemetry (`FontMono`):** `Cascadia Mono`, fallback `Consolas` (`Typography.NumeralAlignment="Tabular"`).

| Role | Size | Weight | Color Tier |
|---|---|---|---|
| `DisplayHero` | `26px` | SemiBold | `TextPrimaryBrush` |
| `Heading1` | `22px` | SemiBold | `TextPrimaryBrush` |
| `Heading2` | `16px` | SemiBold | `TextPrimaryBrush` |
| `BodyPrimary` | `13.5px` | Regular / Medium | `TextPrimaryBrush` / `TextSecondaryBrush` |
| `BodyCompact` | `12.5px` | Medium | `TextSecondaryBrush` |
| `CaptionLabel` | `11px` | SemiBold | `TextTertiaryBrush` |

---

## 4. Spacing, Radii, and Shell Proportions
- **Base Spacing Grid:** `4px` (`4, 8, 12, 16, 20, 24, 32`).
- **Sidebar Width:** `232px` fixed left rail on `#0A0B0E` with `1px` right hairline `#1B1F2A`.
- **Corner Radii Scale:**
  - `RadiusControl`: `4px` (buttons, badges, inputs, toggles)
  - `RadiusCard`: `6px` (structured panels, game cover cards)
  - `RadiusModal`: `8px` (dialogs, inspection drawers)
- **Depth Strategy:** Strictly **Borders-only + 1-step lightness shift** (no fuzzy multi-layer dark-mode shadows).
- **Interactive States:** Every clickable element defines explicit `Default`, `Hover`, `Pressed`, `KeyboardFocus` (2px `#38BDF8` ring), and `Disabled` states.
- **Signature Product Element:** **4-Stage Rendering Pipeline Matrix (`[NR] -> [SR] -> [FG] -> [RR]`)** displayed on Overview, Game Library Cards, and Game Details so users see hardware + runtime + game compatibility in one unified optical schematic.
