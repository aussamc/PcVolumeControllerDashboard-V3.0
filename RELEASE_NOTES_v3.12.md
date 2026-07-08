# v3.12 — Avalonia parity batch: auto sleep/wake + reconnect / diagnostics / overlay fixes

This release closes a batch of Windows feature-parity gaps in the Avalonia host and
fixes several Windows-only defects that surfaced during on-hardware verification.

## Features
- **Auto sleep/wake** — when the PC locks, goes idle, or suspends, the controller is
  told to sleep (OLEDs blank, state pushes suppressed); on unlock/activity/resume it
  wakes and the OLEDs repaint from live state. Windows-only for now (the Linux build
  uses a no-op activity monitor).

## Parity fixes
- **Encoder feel** — the encoder path now debounces and coalesces rapid raw deltas and
  guards against isolated direction reversals (a bouncy detent no longer spams volume
  writes); linked channels still gang together with Volume Smoothing off.
- **Volume overlay mute mode** — muting (via button or the master-mute hotkey) now
  shows a distinct mute layout instead of relabelling the volume bar.
- **Channel-count mismatch warning** — a controller that connects but reports a
  channel count other than the expected 6 now surfaces a colour-coded warning (this
  complements the existing incompatible-firmware/protocol warning).
- **Reconnect cooldowns** — wrong-identity and unopenable ports are backed off with
  per-port cooldowns so a second serial device isn't re-probed every reconnect cycle;
  the real controller still reconnects promptly on unplug/replug.

## Additional fixes (found during verification)
- **Incompatible-controller flicker** — a controller with too-old firmware left
  plugged in no longer flickers the connection status (and no longer resets the ESP32
  every few seconds); it settles into a steady warning and is re-probed quietly so a
  firmware upgrade is still auto-detected.
- **Crash on window close (Windows)** — closing the window with minimise-to-tray off
  (the default) previously crashed with a stack overflow (`OnClosing` re-entered its
  own shutdown path); the app now exits cleanly.
- **WPF host build (Windows)** — the retired WPF host no longer pulls the Linux audio
  backend's sources into its build.

## Notes
- The shipping Windows (WPF) dashboard remains functionally unchanged (version string
  only).

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
