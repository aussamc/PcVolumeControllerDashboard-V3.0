# v3.19 — Automatic update checks

The dashboard now checks for a newer release on its own and tells you when one is
available, instead of only checking when you click **Check for updates**. No settings-file
migration, protocol, or firmware changes — the controller does **not** need a reflash.

## Automatic update checking

- **Background check on launch** (and periodically while the app runs) queries GitHub for
  a newer release. It's throttled so rapid restarts don't re-query, and it's controlled by
  the existing **"Automatically check for updates"** preference (on by default) added in
  v3.18 — turn it off to disable the automatic checks entirely.

- **Update banner** — when a newer version is found, a banner appears at the top of the
  **Audio** tab with three choices:
  - **View release** — opens the release page in your browser.
  - **Skip this version** — stops reminding you about this release until a newer one ships.
  - **Remind me later** — hides the banner until the next check.

- **Desktop notification** — if tray notifications are enabled, you also get a toast when
  an update is available, so you see it even when the window is minimised to the tray.

- **Safe mode** — the automatic check is suppressed under `--safe`, alongside the other
  network/auto behaviours that flag disables.

The manual **Check for updates** button in the Debug/diagnostics section still works exactly
as before.

> **Coming next:** one-click **download & install** of the update (the
> "Automatically download & install updates" preference). This release lands the automatic
> *checker*; the download/apply engine follows.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
