# v3.9 — Avalonia: link groups + multi-app pools (per-channel detail complete)

v3.9 completes the Avalonia per-channel detail feature set. The shipping Windows
(WPF) dashboard is unchanged.

## Features
- **Channel link groups** — give two or more channels the same "Link group" name
  (per-channel detail) and turning any one of them moves them all together
  (ganged volume), honouring each channel's own limits/smoothing.
- **Multi-app pools** — a channel can hold a pool of targets and controls whichever
  one is currently making sound (e.g. a "browser" channel that follows whatever's
  playing). A non-empty pool overrides the single assigned target.

## Scope
- **Named profiles** and **Cycle output device** are not part of the Avalonia port
  (output-device cycling remains on the roadmap for later); their button-action
  options were removed.

## Firmware
No firmware changes. Requires firmware protocol **v2.24** or later.
