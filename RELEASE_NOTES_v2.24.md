# Release Notes — v2.24

## Changes

### Per-channel OLED display mode (firmware + dashboard)

Each channel can now have its own OLED display mode, independent of the global
setting in the OLED Setup tab.

A new **OLED mode** dropdown has been added to the Per-Channel Controls section
in the Audio tab (below the double-press action selector).

| Option | OLED layout |
|--------|-------------|
| **Use global default** | Inherits the mode set in OLED Setup (default for all channels) |
| **App name + volume** | Label top, volume centre, mute + status bottom |
| **Large volume number** | Label top, extra-large volume number, mute bottom |
| **Mute status** | Large MUTED/ACTIVE, label + volume below |
| **App name only** | Channel number, label, status, volume — no large number |
| **Volume bar** | Label top, horizontal progress bar, volume % + mute bottom |

### New protocol command: `DISPMODE,<ch>,<mode>`

The dashboard sends `DISPMODE,<ch>,<mode>` to set the per-channel mode on the
firmware. An empty mode string resets the channel to use the global setting.
All six DISPMODE messages are sent on every connect/reconnect so firmware state
stays in sync with settings.

## Firmware change — reflash required

Firmware v2.24 adds the `channelMode[6]` array and `DISPMODE` handler.
The protocol version is bumped to `2.24` — v2.21 firmware will be rejected.

## Compatibility

- Dashboard: v2.24
- Required firmware protocol: v2.24 (**reflash required**)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
