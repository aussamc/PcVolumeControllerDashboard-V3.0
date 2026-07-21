# v3.23.4 — Settings no longer wiped by a test run

Bug fix: channel assignments and other saved settings could be reset to a blank
configuration — most visibly right after installing an update, which made it look like
the installer failed to carry settings across. It wasn't the installer. Dashboard-only
change; no firmware reflash needed.

## What was wrong

`SettingsRepository` always resolved its paths from the real per-user config directory
(`%APPDATA%\PcVolumeController` on Windows). Three unit tests exercise the real
`Save`/`Load` entry points, so **running `dotnet test` overwrote the live settings.json**
with a test fixture: blank channels 2–6, a single "Gaming" profile, `SettingsVersion 7`,
no remembered COM port or controller chip ID, and every toggle (including Advanced Debug
Features) back at its default.

Because a test run typically happens while preparing a release, the damage surfaced the
next time the dashboard was launched — i.e. straight after the update installed — and
looked exactly like a failed settings migration. The recovered evidence on the affected
machine: eight byte-identical `settings-premigration-*.json` backups spanning 16–21 July,
each matching that fixture exactly, and the last one written at the same minute the test
assembly was rebuilt.

Nothing in the installer, the update path, or the schema migrations was at fault — the
mappings were already gone from disk before the new build ever started.

## The fix

- The per-user config directory is now redirectable, via
  `SettingsRepository.ConfigDirectoryOverride` (in-process) or the
  `PCVOLUMECONTROLLER_CONFIG_DIR` environment variable. Everything else — settings,
  setup backups, logs, diagnostics — derives from it, so one redirect covers all of them.
- The test suite sets that redirect to a throwaway temp folder in a **module
  initializer**, so it applies before any test runs and automatically covers tests added
  later. The three tests that write through the real entry points additionally take their
  own isolated scope.
- Added regression tests asserting the config directory is never the live one during a
  test run, and that the environment variable is honoured.

Nothing changes for normal use: without an override or environment variable the paths are
byte-for-byte what they were.

## Recovering settings lost to this bug

`%APPDATA%\PcVolumeController\setup_backups\` keeps the ten most recent snapshots. A
`settings-premigration-*.json` from *before* the reset can be copied over
`settings.json` with the dashboard closed.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
