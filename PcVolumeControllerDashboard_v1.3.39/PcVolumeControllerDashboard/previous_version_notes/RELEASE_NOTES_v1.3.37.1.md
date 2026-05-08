# PC Volume Controller Dashboard v1.3.37.1

## Purpose

This is a focused bug-fix patch for the ESP32 local sleep behaviour when the controller is powered but the dashboard is not connected.

## Fixed

- Fixed ESP32 no-dashboard sleep not starting if the dashboard never connected after boot.
- Changed the no-dashboard local sleep timeout to 2 minutes.
- Added a local activity timer so encoder/button input wakes the controller and restarts the idle countdown.
- First encoder/button input while sleeping wakes the display only and does not send a control action.
- The controller now returns to normal local-awake mode after wake and will sleep again after another 2 minutes with no dashboard contact or input.

## Versioning

- Dashboard version: `v1.3.37.1`
- Required ESP32 protocol: `v1.3.37.1`
- ESP32 firmware project: `Computer_Volume_Controller_v1.3.37.1`

The ESP32 firmware project was renamed because this release contains a real firmware behaviour change.

## Packaging

- Includes `tools/esptool.exe`.
- Includes ESP32 `.ino` source.
- ESP32 `.bin` artifacts are not included because Arduino CLI / ESP32 board core are not available in the current build environment.
