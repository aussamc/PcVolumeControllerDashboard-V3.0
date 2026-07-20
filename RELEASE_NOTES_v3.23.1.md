# v3.23.1 — Only one dashboard window can ever open

Bug fix: opening the dashboard from the system tray could create **duplicate main
windows** — clicking the tray icon (or the "Show Dashboard" menu item / global
hotkey) at the wrong moment left two dashboard windows open at once. Dashboard-only
change; no firmware reflash needed.

## What was wrong

The dashboard window is built lazily on the first "show" request. Its construction
enumerates WASAPI audio sessions, which blocks on STA COM calls that pump the
Windows message queue — so a second tray click arriving during that construction
(e.g. the second half of a double-click) was dispatched *re-entrantly*, saw the
window field still unset, and built and showed a **second** dashboard window.
Closing the duplicate didn't help much either: with "minimize to tray" on, the
close just hid it, leaving an invisible zombie window running its own channel poll.

## The fix

- All window construction now goes through a single guarded path
  (`App.GetOrCreateMainWindow`): a re-entrant request during construction can no
  longer build a second window — it's remembered and replayed once the one true
  window exists, so the click still lands.
- While the **first-run wizard** is open, tray "Show Dashboard" now focuses the
  wizard instead of building the dashboard beside it (which would also have started
  CHSTATE pushes that overwrite the wizard's OLED identify screens).

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
