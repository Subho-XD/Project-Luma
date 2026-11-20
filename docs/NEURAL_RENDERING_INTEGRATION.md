# Project Luma — Neural Rendering Integration (`gfx1032` / RX 6600 & Upstream v0.9.0)

**Upstream Inspected:** `danielblnc/DLSS-NR-on-AMD` (`v0.9.0`, "Alpha 0.9.0", published `2026-11-18T07:34:12Z`, asset `NeuralHub_v0.9.0_setup.exe`, `30,925,039` bytes)  
**Target Reference Device:** AMD Radeon RX 6600 (`VEN_1002&DEV_73FF`, RDNA 2 / `gfx1032`)  
**AMD HIP SDK 7.2 Documentation Checked:** `https://rocm.docs.amd.com/projects/install-on-windows/en/docs-7.2/reference/system-requirements.html`

---

## 1. Honest Compatibility Findings for `gfx1032` (RX 6600)
1. **AMD Official HIP 7.2 Documentation:** AMD's official Windows system requirements table for ROCm / HIP SDK 7.2 explicitly does **not** list Radeon RX 6600 (`gfx1032`) as an officially supported target. Project Luma records `Hip72OfficiallySupported = false` on RDNA 2 / `gfx1032` and never markets experimental features as officially supported.
2. **Upstream Community Experimental Path (`DLSS-NR-on-AMD v0.9.0`):**
   - Alpha 0.9.0 redesigned the RDNA 2 HIP compute path for Neural Rendering, added a **Superfast** tier for RX 6000 / RX 7000, disables `Reference` mode on RX 6000, and adds the **CC model** (`40–80%` faster, requiring `nvngx_dlssnr.dll >= 310.9.16.0`, whereas HT model requires `>= 310.8.0.0`).
   - Still requires `amdhip64_7.dll` when the mod reports HIP runtime missing, while `DLSS-FG` remains `Unsupported` on RX 6000 (`gfx1032`).
   - On systems with only `amdhip64_6.dll` installed (`10.0.3652.0` detected on the host machine), Project Luma accurately reports **Setup Required / Experimental** and explains which runtime (`amdhip64_7.dll`), user-owned DLL (`nvngx_dlssnr.dll`), and upstream NeuralHub install are absent.

---

## 2. Ten-Stage Activation Pipeline (No False Positives)
Project Luma evaluates and verifies all ten stages rather than marking Neural Rendering active when a UI toggle is clicked:
1. **Hardware Detection:** DXGI + Display Registry + PCI database identifies vendor, architecture, and exact `gfx1032` target.
2. **Runtime Detection:** Probes `System32` and `Program Files\AMD\ROCm\*\bin` for `amdhip64_7.dll` vs `amdhip64_6.dll`.
3. **Scoped Model DLL Discovery:** `UserDllVault.ScanGameLibrariesForUserDllsAsync` searches registered game folders (max depth 3, never scanning whole disks) prioritizing `nvngx_dlssnr.dll`.
4. **Architecture & Build Validation:** Validates PE32+ (`0x8664`), file version (`>= 310.9.16.0` for CC model / `>= 310.8.0.0` for HT), Authenticode signature, and SHA-256.
5. **License-Compliant Backend Resolution:** Never bundles/mirrors `NeuralHub_v0.9.0_setup.exe`; opens the official GitHub Releases page or lets the user connect an existing independent installation.
6. **Game Compatibility Check:** Verifies 64-bit DirectX 12 and absence of blocked anti-cheat.
7. **Game-Specific Integration & Transactional Backup:** Backs up pre-existing files to `.luma_backup/<session>/manifest.json`.
8. **Configuration (`Fast` / `Superfast`):** Writes safe configuration presets matching GPU tier (`Reference` gated off on RDNA 2).
9. **Launch & Startup Guard:** `GameLaunchService` launches after verifying integrity and process lock state.
10. **Verified Runtime Status:** `VerifyInstallationHealthAsync` inspects actual deployed files & SHA-256 hashes before reporting active enhancements in Launch Notifications or Game Hub badges.
