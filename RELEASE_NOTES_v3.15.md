# v3.15 — Volume overlay enhancements

The first post-parity feature batch: the on-screen volume overlay is now customizable.

## Features
- **Transparency** — a slider sets the overlay's opacity (30–100%). The fade-out
  animation now fades from your chosen opacity rather than always from fully opaque.
- **Size** — a slider scales the whole overlay (75–150%): the panel, channel name,
  volume bar, and mute glyph all scale together.
- **Show on all screens** — mirror the overlay on every monitor instead of just the
  primary one. Off by default.
- **Live preview** — a **Preview** button (and adjusting position/transparency/size/
  all-screens) shows a sample overlay so you can see the effect immediately, without
  turning a knob.

Screen position (Top/Bottom × Left/Center/Right) was already supported and is unchanged.

## Notes
- New settings (`OverlayOpacity`, `OverlayScale`, `OverlayAllScreens`) default to the
  previous behavior (100% opacity, 100% size, primary screen only), so existing
  configs look identical until you change them.

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
