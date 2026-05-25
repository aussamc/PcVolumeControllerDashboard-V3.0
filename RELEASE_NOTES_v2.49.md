# Release Notes — v2.49

## Mute overlay with speaker icons

The on-screen overlay now shows mute state when an encoder's mute is toggled.

### What changed

**New mute overlay display mode** — when a channel is muted or unmuted (via button press, hotkey, or any other action), the overlay pops up showing:

- The channel name
- A **speaker icon** (cone + two sound-wave arcs) when the channel is unmuted
- A **muted speaker icon** (cone + cross through the wave area) when the channel is muted
- "Muted" or "Unmuted" label next to the icon

The icons are pure vector paths — no image files, no font dependencies, fully colour-matched to the dark/light theme.

**Volume overlay** — the existing progress-bar display for volume changes is unchanged.

Both overlays use the same position, timeout, and enable/disable settings from the Overlay section of the Setup tab.

---

## Compatibility

- Dashboard: v2.49
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
