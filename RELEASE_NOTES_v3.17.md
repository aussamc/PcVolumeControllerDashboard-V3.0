# v3.17 — Setup-tab restructure & Debug-tab move

A UX-only release that declutters the Setup tab and tidies up a couple of stale bits.
No settings, protocol, or firmware changes — the controller does **not** need a reflash.

## Setup tab

- **Collapsible sections** — the long scroll of Setup cards is now organised into
  expandable sections (Application Setup, Global Hotkeys, Encoder Sensitivity, Encoder
  Feel, Style Settings, Volume Overlay, Audio Backend, Maintenance). Everything is
  collapsed by default except **Application Setup**, so the tab opens compact and you
  expand only what you need. The app info and software-update panel stays pinned at the
  top.

- **"Advanced debug logging" moved to the Debug tab** — it's a diagnostics control, not a
  setup step, so it now lives with the other debug tools. The setting itself is unchanged.

## Cleanup

- Removed the stale "Coming in later PRs" cards from the Audio and Setup tabs — the
  features they described (link groups, multi-app pools, hotkeys, updates, the wizard)
  all shipped long ago.
- Fixed stale helper text on the OLED tab that still referred to serial sync as a future
  change.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
