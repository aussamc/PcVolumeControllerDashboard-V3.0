# Release Notes — v2.35

## New feature: Output device switching

### Audio tab — Output Devices panel
A new compact panel in the Audio tab shows all active Windows audio output devices.
Tick the Cycle checkbox next to each device you want to include in the rotation.
The currently active default device is indicated with a ✓.

### Cycle Output Device button action
A new "Cycle output device" button action is available for short, long, and double
press on all six encoder buttons. When triggered, it advances to the next ticked
device in the list and sets it as the Windows default audio device (all roles:
Console, Multimedia, Communications).

### Overlay notification
When the device switches, the volume overlay briefly shows the new device name.

### Notes
- Requires at least two devices checked in the Cycle list to cycle
- Uses the Windows IPolicyConfig COM interface to set the default endpoint
- The output device list refreshes automatically when the default device changes

## Compatibility

- Dashboard: v2.35
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
