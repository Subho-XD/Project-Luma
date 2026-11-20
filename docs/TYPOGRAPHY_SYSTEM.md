# Project Luma v3.0.0 — Typography System

## 1. Selected Typefaces (Zero `Space Grotesk`)
- **Primary UI & Display Typeface:** **Google Sans** (`assets/fonts/GoogleSans.ttf` from official `https://github.com/google/fonts/tree/main/ofl/googlesans`, embedded via WPF `pack://application:,,,/ProjectLuma.UI;component/Assets/Fonts/#Google Sans`)
  - Used for main headings, game titles, buttons, sidebar navigation, descriptions, installer screens, uninstaller options, dialogs, and notifications.
- **Secondary Technical Metadata Typeface:** **Space Mono** (`assets/fonts/SpaceMono-Regular.ttf`, `assets/fonts/SpaceMono-Bold.ttf` from official `https://github.com/googlefonts/spacemono`, embedded as `#Space Mono`)
  - Used strictly for concise uppercase overlines, GPU/driver identifiers (`gfx1032`), version numbers, SHA-256 verification prefixes, and compact diagnostic metadata. Paragraphs and descriptions never use monospace.

## 2. Type Hierarchy
| Role | Font Family | Size | Weight | Color Token |
|---|---|---|---|---|
| Page & Hub Title | Google Sans | `24px–30px` | SemiBold / Bold | `#F6F6F6` |
| Section Header | Google Sans | `16px–18px` | SemiBold | `#F6F6F6` |
| Card Title / Action | Google Sans | `14px–15px` | SemiBold | `#F6F6F6` |
| Body Copy & Setting Label | Google Sans | `13px–13.5px` | Regular / Medium | `#B8B8B8` / `#F6F6F6` |
| Supporting Subtext | Google Sans | `12px` | Regular | `#828282` |
| Technical Overline & Spec | Space Mono | `10.5px–11.5px` | Regular / Bold | `#ED1C24` / `#B8B8B8` |

## 3. Licensing Compliance
Both fonts are bundled under the **SIL Open Font License 1.1**, with complete upstream `OFL.txt` texts preserved in `assets/fonts/OFL-GoogleSans.txt`, `assets/fonts/OFL-SpaceMono.txt`, and `licenses/` alongside the distributed executable.
