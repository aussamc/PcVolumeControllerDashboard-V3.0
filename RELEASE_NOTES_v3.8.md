# v3.8 — Avalonia: overlay, settings I/O, backend switch, connection controls

v3.8 rounds out the Avalonia host's Setup/Audio surface. The shipping Windows
(WPF) dashboard is unchanged.

## Features
- **On-screen volume overlay** — a transient popup at a configurable screen corner
  shows the channel + volume bar + percentage when a knob/preset/mute changes,
  auto-hiding after the timeout.
- **Settings import / export** — save the current settings to a JSON file and
  re-import one (with confirmation). Core `SettingsRepository.ExportTo`/`ImportFrom`
  are unit-tested.
- **Audio backend switch** — WASAPI ↔ VoiceMeeter from Setup, switched live via a
  swappable backend wrapper (falls back to a null backend if a backend fails to
  start).
- **Reconnect / Disconnect controls** on the Audio tab.
- **About dialog** — version / protocol / connected-controller info + a link to
  the project page.

## Firmware
No firmware changes. Requires firmware protocol **v2.24** or later (v2.25 current,
backward-compatible).
