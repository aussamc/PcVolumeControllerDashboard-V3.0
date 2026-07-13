# v3.14.2 — `--safe` diagnostic launch + manual per-port picker (N1, N2)

Two small polish items on top of v3.14.1.

## Features
- **`--safe` diagnostic launch flag** — start the app with `--safe` to troubleshoot a
  misbehaving setup: auto-connect and the reconnect loop are skipped, and all audio-
  control writes (encoder, button, presets, and the master volume/mute hotkeys) are
  suppressed, so the app won't drive the hardware or change any volumes. Reads and the
  live state display still work; a banner on the Controller card shows when safe mode is
  active. Use **Reconnect** or the new port picker to connect manually.
- **Manual per-port picker** — the Controller card now has a serial-port dropdown and a
  **Connect to port** button, so you can connect to a specific port instead of relying on
  auto-detect (handy when a second serial device is plugged in). The dropdown refreshes
  when you open it.

## Notes
- The shipping Windows (WPF) dashboard remains functionally unchanged (version string
  only).

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
