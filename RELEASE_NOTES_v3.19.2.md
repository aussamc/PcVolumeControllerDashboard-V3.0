# v3.19.2 — Structured, level-based logging

A logging overhaul that brings the diagnostics log up to standard practice, so a log you
share for support is complete and self-describing. No settings-file migration, protocol, or
firmware changes — the controller does **not** need a reflash.

## What changed

- **Severity levels.** Every line is now tagged `INFO` / `WARN` / `ERROR` (and `DEBUG` when
  advanced logging is on), so problems stand out instead of being buried in prose. "Advanced
  debug logging" is now simply a **verbosity threshold** — turning it on lowers the bar to
  include the verbose `DEBUG` detail (raw serial TX/RX, per-encoder changes, button-press
  mappings); turning it off keeps `INFO` and above.
- **Startup banner.** Each log opens with a self-describing header: app version, OS and
  architecture, .NET runtime, launch mode (normal / `--safe`), advanced-logging state, audio
  backend, and the settings path. A shared log now states exactly what produced it.
- **Component tags & thread ids.** Lines carry a `[Serial]` / `[Audio]` / `[Update]`
  category where useful and the originating thread id, so you can filter by subsystem and
  tell the serial-read thread from the UI thread when diagnosing timing issues.
- **Full error detail.** Errors are logged with their exception message at `ERROR`, and the
  full stack trace is added at `DEBUG` (i.e. when advanced logging is on) — readable normally,
  fully diagnosable when you need it.
- **Unambiguous timestamps.** Timestamps are now ISO 8601 with the UTC offset
  (`2026-07-15T20:18:34.855+10:00`) instead of a bare local time.
- **Bounded log folder.** In addition to the existing 7-day cleanup, the logs folder is now
  capped by total size (oldest files pruned first), so a long verbose session can't let it
  grow without bound.

Nothing you do changes: the toggle still lives on the Debug tab and defaults off, and normal
logging looks the same apart from the new level/format.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
