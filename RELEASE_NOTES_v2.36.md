# Release Notes — v2.36

## New feature: Per-channel volume presets

Each of the 6 encoder channels now has three named volume presets, each storing a
name (up to 16 characters) and a volume percentage (0–100%).

### Configuration
Presets appear in the Per-Channel Controls panel (Audio tab, right column) under a
new "Volume Presets" section. Adjusting the slider saves immediately; names save on
focus-leave.

### Button actions
Three new button actions are available for short, long, and double press:
- **Apply preset 1** — jumps the channel's volume to preset 1's value
- **Apply preset 2** — jumps to preset 2
- **Apply preset 3** — jumps to preset 3

If volume smoothing is enabled, the jump animates smoothly to the target volume.
The volume overlay shows the new volume level when a preset fires.

## Compatibility

- Dashboard: v2.36
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
