# v3.23.3 — Volume overlay always stays on top

Bug fix: the on-screen volume overlay could appear **underneath** other applications —
noticed with Autodesk Fusion, but any app that claims an always-on-top window (CAD
viewports and palettes, games, capture/streaming overlays) could cover it. Dashboard-only
change; no firmware reflash needed.

## What was wrong

The overlay window asked Windows for "always on top" **once, when it was first
created**. Windows keeps a separate stacking order *within* the always-on-top group:
whichever window claimed the flag most recently sits above the others. So the moment
another application raised an always-on-top surface of its own — as Fusion does —
that surface outranked the overlay, and nothing ever moved the overlay back up. Every
later volume change re-showed the overlay behind it.

## The fix

The overlay now **re-asserts always-on-top every time it is shown**, so it re-takes
the top of the stacking order on each knob turn / preset / mute. The re-assert is
non-activating — it still never steals keyboard focus from the app you're working in.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
