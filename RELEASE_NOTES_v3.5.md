# v3.5 — Phase 1: Avalonia device state push (live OLEDs)

v3.5 closes the biggest remaining functional gap in the cross-platform Avalonia
host: it now pushes live state back to the controller, so the physical OLEDs and
display reflect what the dashboard sees. The shipping Windows (WPF) dashboard is
unchanged.

## Device state push (PC → ESP32)

- **Per-channel `CHSTATE`**: each channel's name, live volume, mute, and status
  are pushed to the controller, driving every OLED. Refreshed from the Audio
  tab's ~2×/second poll with change detection, so only genuinely changed lines
  hit the serial link.
- **`STATE` for the selected channel**: mirrors the WPF host's active-channel
  line for the channel selected in the Audio grid.
- **OLED configuration (`OLEDCFG`)**: display mode, brightness, disconnected
  sleep timeout, connected-idle action/timeout, and anti-burn-in are pushed
  whenever you change them in OLED Setup.
- **Per-channel OLED mode (`DISPMODE`)** is pushed on connect.
- On (re)connect the host pushes OLED config + per-channel modes immediately and
  re-sends all channel states, so a freshly connected controller populates
  without waiting for the next poll.

## Internals

- New `Core.ProtocolMapping` (pure, unit-tested) centralises the
  domain-constant → wire-string translations (display modes, idle actions,
  protocol-safe labels) shared by every host.
- New `App.Avalonia` `DeviceStateService` owns the outbound push with change
  detection; `SerialConnectionService` gained a guarded `SendLine`.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** or later (v2.25
current, backward-compatible).
