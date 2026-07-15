# v3.19.3 — Tidy up downloaded update installers

A small housekeeping fix to the built-in updater. It no longer leaves old installer files
behind in the temp folder, so update downloads no longer accumulate on disk over time. No
settings-file migration, protocol, or firmware changes — the controller does **not** need a
reflash.

## What changed

- **Old update downloads are pruned after each download.** The updater downloads each
  installer into a temp folder (`%TEMP%\PcVolumeController-update\`); because every release
  has a version-stamped filename, older ones used to pile up there until Windows' own disk
  cleanup ran. After a successful, verified download the updater now removes any older files
  in that folder, leaving only the current one.
- **The folder is also swept on launch.** On every start the updater clears out any installer
  left behind by an already-applied update or an abandoned download, so nothing lingers
  between sessions.

Both passes are best-effort and safe — a file that's still in use (e.g. an installer mid-run)
is skipped, and a cleanup hiccup can never fail an otherwise-good download.

This only affects the updater's temp folder. The installed app has always upgraded in place
(one install, one uninstaller entry) — that's unchanged.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
