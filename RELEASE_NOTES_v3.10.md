# v3.10 — Avalonia: first-run wizard, global hotkeys, and update check

v3.10 closes the last user-facing gaps of the cross-platform Avalonia port: a
guided first-run setup, system-wide hotkeys, and a software update check. The
shipping Windows (WPF) dashboard is functionally unchanged (version string only).

## Features
- **First-run setup wizard** — on a brand-new install the dashboard opens a guided
  wizard (welcome → connect/pair the controller → check the OLED displays → map
  each knob to an app/device → done). Re-launchable any time from **Setup →
  Run setup wizard again**.
- **Global (system-wide) hotkeys** — assign shortcuts that work even when the
  dashboard is in the background: master volume up/down, toggle master mute, and
  show dashboard. Set them under **Setup → Global Hotkeys** (click Set, then press a
  key combination). Windows only for now (Linux/macOS land with their platform
  layers).
- **Software update check** — **Setup → Software Updates → Check for updates** queries
  GitHub for a newer release and offers a link when one is available.

## Notes
- Global hotkeys use Windows `RegisterHotKey`; other platforms report no hotkeys
  until their layers ship.
- The first-run wizard also runs after a factory reset (settings are wiped), and can
  be re-run without losing your current setup via the Setup-tab button.

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
