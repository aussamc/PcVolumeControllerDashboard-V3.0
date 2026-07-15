# v3.19.3 — Tidy up downloaded update installers

A small housekeeping fix to the built-in updater. When it downloads a new release, it now
deletes the previously-downloaded installer from the temp folder instead of leaving it
behind, so update files no longer accumulate on disk over time. No settings-file migration,
protocol, or firmware changes — the controller does **not** need a reflash.

## What changed

- **Old update downloads are pruned.** The updater downloads each installer into a temp
  folder (`%TEMP%\PcVolumeController-update\`); because every release has a version-stamped
  filename, older ones used to pile up there until Windows' own disk cleanup ran. After a
  successful, verified download the updater now removes any older files in that folder,
  leaving only the current one. Best-effort and safe — a file that's still in use (e.g. an
  installer mid-run) is skipped, and a cleanup hiccup can never fail an otherwise-good
  download.

This only affects the updater's temp folder. The installed app has always upgraded in place
(one install, one uninstaller entry) — that's unchanged.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
