# v3.19.1 — Advanced debug logging captures the full picture

A small diagnostics improvement: the **Advanced debug logging** toggle (Debug tab) now
actually captures what you need to diagnose a controller problem. No settings-file
migration, protocol, or firmware changes — the controller does **not** need a reflash.

## What changed

Previously "Advanced debug logging" only added a few per-volume-change lines to the log,
and the raw serial traffic was visible **only** in the live Debug console — it was never
saved, so it was gone the moment you closed the app or it scrolled off. Now, when the
toggle is on, this session's log file (`avalonia-*.log`) captures:

- **The full raw serial conversation** — every `TX` (PC → controller) and `RX`
  (controller → PC) line, exactly as the live Debug console shows them, including the
  `STATE`/`CHSTATE` display pushes and the incoming `ENC`/`BTN`/`PONG` messages.
- **Per-encoder volume changes** and **every button-press → action mapping** (not just the
  "no action" case), so a "my knob/button does the wrong thing" issue shows what the press
  actually dispatched to.

This makes the log file a complete, shareable record for troubleshooting — turn the toggle
on, reproduce the problem, then attach the log (or use **Export diagnostics**).

It stays **off by default** (it's verbose) and only affects what's written while it's on;
normal connection, update, and lifecycle logging is unchanged.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
