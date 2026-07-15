# v3.19.5 — Click a notification to open the dashboard

Desktop notifications are now clickable: clicking one brings the dashboard to the
foreground. Previously clicking a notification did nothing. No settings-file migration,
protocol, or firmware changes — the controller does **not** need a reflash.

## What changed

- **Notifications open the dashboard on click.** Clicking a toast (controller connected/
  disconnected, "started minimised to tray", or "update available") now shows and focuses
  the dashboard window, the same as clicking the tray icon — handy when the app is running
  minimised to the tray.

Windows only for now: the click-through is wired through the Windows toast activator. Linux
`notify-send` notifications remain click-inert (that needs a persistent notification-server
connection, out of scope here).

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
