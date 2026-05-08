# PC Volume Controller Dashboard v1.3.38 Beta 1

This is the first beta build for the v1.3.38 OLED preview and display-settings release.

## Build type

```text
Dashboard version:       v1.3.38 Beta 1
Required ESP32 protocol: v1.3.37.1
ESP32 firmware source:   Computer_Volume_Controller_v1.3.37.1
```

The ESP32 firmware project was intentionally not renamed for this beta because the changes are dashboard-side only. Rename the ESP32 firmware project only if a future beta adds real firmware/protocol/display-driver behaviour changes.

## Added

- Added an **OLED Preview** dashboard tab.
- Added one preview panel for each of the six controller channels.
- Added dashboard-side display mode selection:
  - App name + volume
  - Large volume number
  - Mute status
  - App/device name
  - Simple bar/percentage view
- Added dashboard-side OLED brightness setting.
- Added dashboard-side OLED dim/sleep timeout setting.
- Saved the new display settings in the existing setup JSON.

## Changed / cleaned up

- Updated dashboard version string to `1.3.38 Beta 1`.
- Kept required ESP32 protocol at `1.3.37.1`.
- Coalesced full STATE updates after setup import/factory reset so the dashboard sends one consolidated refresh instead of immediate repeated updates.
- Added throttling for repeated Hardware Test SLEEP/WAKE test commands.
- Kept Advanced Debug Logging off by default.

## Not included yet

- No ESP32 OLED brightness command handling yet.
- No ESP32 display mode command handling yet.
- No SSD1315/multi-OLED firmware driver update yet.
- No compiled ESP32 `.bin` artifacts are included from this environment; use `firmware_bin/build_instructions.txt` if firmware binaries are needed.

## Packaging notes

- `tools/esptool.exe` is included.
- Firmware source is included under `Computer_Volume_Controller_v1.3.37.1/`.
