# v3.21 — Audio tab declutter

Reorganises the **Audio** tab so the everyday controls are front-and-centre and the
one-time setup lives with the other setup options. No behaviour change to audio,
channels, or the controller — this is a layout-only pass. App-only; no firmware change
or reflash.

## What changed

- **Per-channel detail is now grouped into collapsible sections.** The long single
  form under a selected channel is split into **Assignment & Basics** (open by default:
  target assignment, display name, button actions), **Volume** (sensitivity, limits,
  presets), and **Display & Advanced** (OLED mode, link group, multi-app pool). The
  common controls are visible at a glance; the rarely-touched ones are one click away.

- **Target assignment folded into the channel detail.** The separate "Assign Target"
  card is gone — picking a target now lives at the top of the selected channel's
  **Assignment & Basics** section, so everything about a channel is in one place.

- **Connection controls moved to the Setup tab.** Reconnect / Disconnect and the manual
  per-port picker now live under **Setup → Controller Connection**, alongside the other
  one-time setup options (and they're already covered by the first-run wizard). The
  Audio tab keeps a compact, always-visible **connection status banner** so you can still
  see connected/disconnected at a glance.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
