# v3.14 — Cross-platform desktop notifications (F6)

This release makes the dashboard's "tray notifications" preference functional on the
Avalonia host — the last P1 parity gap versus the Windows (WPF) host.

## Features
- **Desktop notifications** — with **Show tray notifications** enabled, the dashboard now
  raises a desktop notification when the controller **connects** or **disconnects**, and
  when the app **starts minimised to the tray**. Previously this checkbox did nothing on
  the Avalonia host.
  - **Windows** — a modern toast (appears bottom-right and in the Action Center; no extra
    tray icon).
  - **Linux** — a standard desktop notification via `notify-send` (libnotify).
  - **macOS** — deferred (no-op for now).

## Notes
- Notifications are best-effort: if no notification mechanism is available (e.g.
  `notify-send` isn't installed on Linux), the app logs it and carries on.
- The shipping Windows (WPF) dashboard remains functionally unchanged (version string
  only).

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
