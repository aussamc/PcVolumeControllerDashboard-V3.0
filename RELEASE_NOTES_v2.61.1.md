# v2.61.1 — Fix settings not persisting across shutdown/restart

## Bug fixes

- **Settings now persist when the PC is shut down or restarted.** Several Setup-tab
  settings (OLED brightness / sleep & idle timeouts / display mode, encoder
  acceleration and smoothing, theme, window size, splitter position, selected
  channel, and the Setup checkboxes) are only written to disk by `FlushUiToSettings()`,
  which previously ran almost entirely from the window's graceful-close handler
  (`OnClosing`). In normal use the dashboard lives in the system tray and is
  terminated by Windows at shutdown/restart/logoff **without** a graceful close, so
  that flush never ran and those settings reverted to their previous values on the
  next launch. The dashboard now subscribes to `SystemEvents.SessionEnding` and
  flushes settings to disk before the OS terminates the process.

  *(Channel assignments, COM port, paired controller chip ID, and profiles were
  unaffected — those already save immediately when changed.)*

- **Atomic settings writes.** `SettingsRepository.Save` now writes to a temporary
  file and moves it into place, so a shutdown that interrupts a write can no longer
  leave `settings.json` half-written (which would otherwise be read back as corrupt
  and reset everything to defaults on the next launch).

## Firmware

No firmware changes. Requires firmware protocol **v2.24** (unchanged).
