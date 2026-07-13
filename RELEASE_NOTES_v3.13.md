# v3.13 — Avalonia gated Debug tab (hardware self-test + diagnostics + log helpers)

This release folds the remaining developer/troubleshooting parity items into the
existing Avalonia **Debug tab**, gated so it stays out of the way for everyday use.

## Features
- **Advanced Debug Features toggle** — a new Setup option (off by default) shows or
  hides the whole Debug tab. Launching with `--debug` force-shows it for that session
  regardless of the setting.
- **Hardware self-test** (Debug tab) — a per-channel checklist that tallies encoder
  turns and button presses (`Channel N: encoder count X, button seen yes/no`) so you
  can confirm all six encoders and buttons physically register, with a **Reset** button
  and **Sleep/Wake test** buttons plus a status readout.
- **Diagnostics readout** (Debug tab) — connection state, COM port, last-heartbeat age,
  protocol vs required, reported-vs-expected channel count, last ESP32 message, and last
  state sent. (The colour-coded protocol/channel-mismatch *warning* already lives on the
  main connection status line from v3.12 and is unchanged.)
- **Log helper buttons** (Debug tab) — copy the serial console, copy the log-folder
  path, and open the current log file with the OS default handler.

## Notes
- The diagnostics-**export** entry point (the diagnostics `.zip`) remains on the
  always-visible Setup tab, so it's reachable even with the Debug tab hidden.
- The shipping Windows (WPF) dashboard remains functionally unchanged (version string
  only).

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
