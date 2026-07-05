# v3.10.1 — Fix: hotkey picker dialog never captured keyboard input

The Avalonia hotkey picker (**Setup → Global Hotkeys → Set…**) never registered a
key press: the dialog window never took keyboard focus on open, so its key-down
handler never saw any input and the combo display stayed stuck on "Press a key…".

## Fixes
- **Hotkey picker dialog now captures key presses** — the dialog explicitly takes
  focus on open, so pressing a key combination now updates the display and enables
  Save as expected.

## Notes
- Also relabels the "Start program on Windows startup" checkbox to "Start program at
  login" ahead of planned Linux/macOS autostart support (still Windows-only for now).
- The shipping WPF dashboard is functionally unchanged (version string only).

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
