# Release Notes — v2.40

## Architecture: extract SettingsRepository

All settings I/O and migration logic has been moved out of `MainWindow` into a
dedicated `SettingsRepository` static class, following the same service-extraction
pattern established in v2.37 (`SerialService`) and v2.38 (`AudioService`).

### What moved into `SettingsRepository`

| Member | Description |
|---|---|
| `GetPath()` | Returns the settings file path in `%APPDATA%\PcVolumeController\settings.json` |
| `GetBackupDirectory()` | Returns the backup folder path |
| `Load(channelCount, maxSensitivity)` | Reads, deserialises, and normalises settings; returns a `LoadResult` record with `Settings`, `WasCorrupt`, `LatestBackupPath`, and `MigrationApplied` flags |
| `Save(settings, logger?)` | Serialises to disk; errors reported via callback, never thrown |
| `Backup(reason, logger?)` | Copies the current settings file to the backup directory, prunes to 10 most recent |
| `Normalize(settings, channelCount, maxSensitivity)` | All schema migrations (v0→v5) and value clamping; returns `true` if any migration was applied |

### Changes to `MainWindow`

- `LoadSettings()` reduced to ~30 lines — calls `SettingsRepository.Load()`, handles the corrupt-file fields, runs the legacy `ChannelTargetKeys` migration, re-saves if `MigrationApplied`
- `SaveSettings()` → single-line delegate: `SettingsRepository.Save(_settings, Log)`
- `BackupCurrentSettingsFile(reason)` → single-line delegate: `SettingsRepository.Backup(reason, Log)`
- `GetSettingsPath()` removed; callers use `SettingsRepository.GetPath()`
- `NormalizeSettings()` (~195 lines) removed entirely; callers use `SettingsRepository.Normalize()`
- `IsValidChannelOledMode()` promoted to `DisplayModes.IsValidChannelMode()` — the correct semantic home — and the old private method removed

### Why this matters

`MainWindow.xaml.cs` now contains no file I/O or JSON serialisation for settings; all
persistence concerns are in one testable, independently readable class. The `LoadResult`
record makes the load contract explicit and removes the need for the `_settingsWereCorrupt`
/ `_settingsCorruptBackupRestored` tuple to be passed as side-channels through fields.

## Compatibility

- Dashboard: v2.40
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
