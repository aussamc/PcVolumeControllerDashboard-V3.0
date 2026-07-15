# v3.19.4 — Log configuration changes

The diagnostics log now records the changes you make in the dashboard, so a shared log shows
not just what the controller and app did, but what was reconfigured. No settings-file
migration, protocol, or firmware changes — the controller does **not** need a reflash.

## What changed

- **Settings changes are logged.** Whenever settings are saved, each changed value is written
  to the log under a `[Settings]` tag — e.g. `Ch3.TargetKey: (empty) -> PROC:chrome`,
  `AudioBackendMode: WASAPI -> VoiceMeeter`, `StartWithWindows: on -> off`.
- **Discrete changes at `INFO`; slider values at `DEBUG`.** Meaningful one-off changes
  (channel assignments, option toggles, modes, hotkeys) are logged at `INFO`, so they appear
  in a normal log. Continuous drag-style inputs (sensitivity, OLED brightness, overlay
  opacity/scale, custom-curve values, timeouts) are logged at `DEBUG`, so they only appear
  when **advanced debug logging** is on and don't clutter a normal log with every intermediate
  value.
- **Bulk operations log one line.** Importing settings or resetting to defaults logs a single
  summary line rather than a field-by-field dump. Incidental UI state (window size, splitter
  position, last COM port, etc.) is not logged.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
