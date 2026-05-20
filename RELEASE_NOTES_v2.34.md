# Release Notes — v2.34

## New feature: On-screen volume overlay

When an encoder knob is turned, a translucent floating panel appears briefly on-screen
showing the channel name, a volume bar, and the percentage. It fades out automatically
after a configurable timeout.

### Configuration (Setup tab → Volume Overlay section)
- **Show volume overlay** checkbox — enable/disable the feature
- **Screen position** — choose from six positions: Top/Bottom × Left/Center/Right; default is Bottom Center
- **Dismiss after** — slider from 1–8 seconds; default is 2.5 s

### Design
- Follows the active theme: dark translucent card in dark mode, bright frosted card in light mode
- Fade-in over 180 ms, fade-out over 350 ms
- Always on top, does not appear in the taskbar, does not receive mouse clicks

## Compatibility

- Dashboard: v2.34
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
