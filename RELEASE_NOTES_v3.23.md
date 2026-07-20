# v3.23 — 2-D software anti-burn-in jitter (firmware v2.31)

Replaces the anti-burn-in pixel-shift method on the controller OLEDs and mirrors it
in the dashboard's controller preview. **Reflash to firmware v2.31 required to get
the new shift on the device** (older firmware keeps the old vertical hardware shift
and still works — the wire protocol is unchanged).

## What changed

Since v2.24 the firmware fought OLED burn-in with the hardware `SETDISPLAYOFFSET`
command: a vertical-only 0–3 px shift that **wraps** the panel, so every display
mode had to reserve the bottom 3 rows or its lowest pixels would reappear at the
top of the screen. v3.16 items 10/11 worked around that with per-mode margin
bookkeeping; the cleaner replacement was tracked in the feature backlog and ships
now.

### Firmware v2.31

- **2-D software jitter** — the drawing origin of every screen walks a 3×3 grid of
  offsets (0–2 px right, 0–2 px down), stepping to an adjacent position every 30 s
  and covering all 9 positions every 4.5 min. Content is **clipped at the panel
  edge, never wrapped** — and every screen keeps its base content inside
  x0..125 / y0..61, so a full jitter never clips a lit pixel either. Burn-in
  coverage improves too: the shift now drifts horizontally as well as vertically.
- **Persistent screens redraw when the jitter steps** — including the
  "Waiting for PC" splash. (The old hardware offset moved the panel without a
  redraw; it also only advanced when the dashboard happened to push a state
  change, so a static screen could sit unshifted indefinitely. The jitter now
  steps from the main loop on its own clock.)
- **Display-fit audit fixes** (found while verifying every mode fits the panel):
  - The **DISPLAY TEST** screen's bottom line sat at y56 (rows 56–62), past the
    row-60 reserve — the old shift could wrap its bottom rows. Moved to y54.
  - The **LARGE_VOLUME** size-2 header now truncates to 10 characters. Labels
    arrive up to 18 chars from the dashboard; at 12 px/char anything over 10 ran
    off the 128 px panel.
  - Full-width rules are now 126 px wide so the 0–2 px x-jitter never clips them.

### Dashboard

- The **Whole Controller Preview** mirrors the new jitter pixel-for-pixel
  (`Core/OledRenderer`): same 3×3 walk, same 30 s cadence, on the PC wall-clock.
  On pre-v2.31 firmware the physical device still uses the old vertical wrap, so
  the preview's shift direction won't match until reflash.
- The renderer applies the same LARGE_VOLUME 10-char header truncation and
  126 px rules, and its layouts are covered by new tests asserting every mode
  (including maximum-length 18-char labels) stays inside the jitter-safe region.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged — v2.31 is a
  display-only change; reflash needed only for the new anti-burn behaviour).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
