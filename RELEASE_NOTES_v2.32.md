# Release Notes — v2.32

## UI completeness

### Dismissible warning banner
A dismissible amber banner now appears below the header when an audio device or serial
connection error occurs. The banner shows a plain-English explanation of the problem and
can be dismissed with the ✕ button. It clears automatically when the audio device
becomes available again.

### Tooltips on all interactive controls
Every button, checkbox, combobox, slider, and text input now carries a tooltip
describing what it does. Tooltips follow the standard WPF 500 ms delay so they do not
intrude during normal use.

### Access keys on primary buttons
The Assign, New Profile, Save OLED Setup, and Flash Firmware buttons now have access
keys (underlined letters) reachable via Alt+key, supporting keyboard-only workflows.

### Display name character counter
The Channel Display Name field now enforces a 16-character maximum and shows a live
character count (e.g. "8 / 16") below the field. The count turns red when the limit is
reached.

## Compatibility

- Dashboard: v2.32
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
