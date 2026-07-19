# v3.22.4 — "Start program at login" actually starts the program at login

Fixes the Setup-tab **Start program at login** toggle silently doing nothing on some
machines. App-only; no firmware change or reflash.

## The bug

The app wrote its `HKCU\...\CurrentVersion\Run` entry correctly, but Windows keeps a
separate per-entry enable/disable flag under
`HKCU\...\Explorer\StartupApproved\Run` (the Task Manager / Settings > Startup Apps
toggle). Once that flag is set to *disabled* — by a click in Task Manager, or by
Windows itself — Windows ignores the Run entry entirely. The app never checked or
cleared this flag, so the in-app checkbox could be ticked while the app never
launched at login, with no indication why.

## The fix

- Toggling **Start program at login** on (Setup tab or first-run wizard) now also
  clears a Startup-Apps "disabled" flag on the entry, so an explicit user enable
  always wins.
- Toggling it off removes the Startup-Apps record along with the Run entry, so a
  future enable starts from a clean slate.
- The passive per-launch registry re-sync does **not** override a Task-Manager
  disable (that's the user's OS-level choice); instead it now logs a clear warning
  that the two settings conflict and how to resolve it.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
