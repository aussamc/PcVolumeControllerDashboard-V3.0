# v3.7 — Phase 1: Avalonia Audio tab per-channel detail

v3.7 adds the per-channel configuration panel to the Avalonia Audio tab — the UI
for the settings the encoder/button runtime consumes. The shipping Windows (WPF)
dashboard is unchanged.

## Per-channel detail panel

Select a channel in the grid to edit, for that channel:

- **Display name** — drives the grid, the OLED label (CHSTATE), and previews.
- **Button actions** — short / long / double press, each chosen from the full
  action list (toggle mute, presets, media keys; profile/output-device/select
  are listed for parity and land with their subsystems).
- **Encoder sensitivity** — per-channel override (or inherit the global value).
- **Volume limits** — min/max percent the encoder and smoothing clamp to.
- **Volume presets** — the three name + volume presets that Apply Preset 1/2/3
  jump to.
- **OLED display mode** — per-channel override (or use the global default),
  pushed to the controller live (DISPMODE).

All edits save immediately and feed the v3.6 encoder/button runtime. A loading
guard mirrors the Setup tab so populating the panel never writes stale values
back.

## Still to come

Channel link groups, multi-app pools, named profiles, output-device cycling, and
connect/disconnect controls land in follow-up PRs.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** or later (v2.25
current, backward-compatible).
