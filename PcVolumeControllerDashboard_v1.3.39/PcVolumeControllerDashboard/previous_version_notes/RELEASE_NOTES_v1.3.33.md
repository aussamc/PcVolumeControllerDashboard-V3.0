# PC Volume Controller Dashboard v1.3.33

## Suggested Git tag

`v1.3.33`

## Suggested commit message

`Add firmware tools, hardware test mode, debug console, and recovery diagnostics`

## Summary

This release combines the planned developer/debug/recovery tooling into one larger release.

## Changes

- Dashboard version updated to `v1.3.33`.
- Required ESP32 protocol updated to `v1.3.33`.
- ESP32 firmware project renamed to `Computer_Volume_Controller_v1.3.33` because a real firmware command was added.
- Added Firmware tab with firmware update/flasher UI scaffold.
- Added Hardware Test tab with encoder/button counters and display test controls.
- Added Debug tab with serial protocol console.
- Added optional heartbeat visibility in debug console.
- Added debug snapshot save/copy/clear tools.
- Added crash log handling for unhandled exceptions.
- Added `--safe` launch option.
- Added diagnostics export zip button.
- Added Open Current Log and Copy Log Folder Path controls.
- Added Clear Remembered Controller control.
- Added Factory Reset Setup control.
- Added Advanced Debug Logging setting.
- Firmware now supports `TEST_DISPLAY` command.

## ESP32 firmware binaries

Compiled ESP32 `.bin` files are not included because Arduino CLI / ESP32 Arduino core is not installed in the current build environment. Firmware source and build instructions are included in:

- `Computer_Volume_Controller_v1.3.33/`
- `firmware_bin/build_instructions.txt`
