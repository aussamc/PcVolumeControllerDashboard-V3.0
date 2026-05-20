# Release Notes — v2.33

## New features

### About dialog
An About dialog is now accessible via the About button at the bottom of the Setup tab.
It shows the dashboard version, connected firmware version and protocol, a link to the
GitHub repository, and third-party component credits (NAudio, System.IO.Ports,
ESP32 Arduino Core, esptool.py).

### Audio tab empty state
When the controller is not connected, the Audio tab channel area now shows a friendly
placeholder with guidance instead of an empty list. The placeholder is replaced by the
real content as soon as the controller connects.

## Compatibility

- Dashboard: v2.33
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
