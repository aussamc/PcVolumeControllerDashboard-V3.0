# v3.19.6 — Channel mappings survive an update

Fixes a bug where installing an update could reset all channel assignments (and the
other per-channel settings) back to default. Your existing `settings.json` is read in
place — no re-configuration, protocol, or firmware change is needed, and the controller
does **not** need a reflash.

## What changed

- **Channel mappings no longer reset on update.** Channel settings are stored both on the
  top-level `Channels` array (what the dashboard actually reads and edits) and, as a
  passive mirror, inside the descoped named-profile system. On load the app was making the
  *profile* copy authoritative, so if the two ever diverged — for example when a new build
  first read a settings file written by an older/WPF-era version — the stale profile copy
  silently overwrote your real mapping, resetting every channel to default. The top-level
  `Channels` array is now authoritative and the profile is mirrored *from* it, so the
  mapping you configured always wins.
- **Pre-migration backup safety net.** Before the app rewrites `settings.json` to apply a
  schema migration, it now snapshots the previous file into the `setup_backups` folder
  (next to `settings.json`), so a future migration issue is recoverable rather than
  silently overwriting the original.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
