# Release Notes — v2.26

## Changes

### Single-instance enforcement
A named system mutex now prevents two copies of the dashboard running simultaneously.
If a second instance is launched it brings the existing window to the foreground and
exits immediately, preventing serial port conflicts and settings file corruption.

### Improved crash handler
The application crash handler (`App.xaml.cs`) now:
- Shows a polished **"Unexpected Error"** dialog with the error message, crash log path,
  and a **Copy Details** button that puts the full stack trace on the clipboard.
- Sets `args.Handled = true` so the OS no longer shows a generic "application stopped
  working" dialog.
- Calls `timeEndPeriod(1)` before shutdown to restore the Windows multimedia timer
  resolution even when the crash bypasses the normal exit path.

### Settings file corruption recovery
If `settings.json` exists but cannot be parsed at startup, the dashboard now:
1. Starts the session with factory defaults.
2. After the window is fully visible, shows a dialog explaining the problem.
3. Offers to **restore the most recent auto-backup** or **start fresh** — the user
   chooses. If no backup exists, defaults are saved automatically with a notification.

### `ProtocolCommands` static class
All serial protocol string literals (`"HELLO"`, `"CHSTATE"`, `"DISPMODE"`,
`"APP_VOLUME"`, etc.) have been extracted into a single `ProtocolCommands` static class
with named `const string` fields. Protocol changes now require editing one class only.

### Unified settings save method
`SaveSettingsFromCurrentState` and `SaveSettingsFromUi` — two near-identical methods
where the former was missing seven acceleration/smoothing fields — are replaced by a
single null-safe `FlushUiToSettings()` that covers all settings fields uniformly.

### Modernised dispatcher calls
All 18 `Dispatcher.BeginInvoke(new Action(() => ...))` calls updated to
`Dispatcher.InvokeAsync(() => ...)` — the current idiomatic pattern.

### Auto-backup file cap
`BackupCurrentSettingsFile` now prunes the oldest backups after creation, keeping a
maximum of 10 files in the `setup_backups` folder.

## Compatibility

- Dashboard: v2.26
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
