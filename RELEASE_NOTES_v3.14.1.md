# v3.14.1 — Assignable-target picker auto-refresh (Q2)

A small parity/quality fix on top of v3.14.

## Fixes
- **Assignable-target picker auto-refreshes** — a newly-launched app now appears in the
  **Assign Target** dropdown (and the per-channel multi-app pool picker) on its own,
  within a couple of seconds, instead of only after clicking **Refresh**. The picker also
  refreshes immediately when the default audio device changes. Matches the Windows (WPF)
  host's behaviour.
  - The dropdown is only rebuilt when the set of available targets actually changes, so an
    open dropdown or an in-progress selection is never disrupted.

## Notes
- The shipping Windows (WPF) dashboard remains functionally unchanged (version string
  only).

## Compatibility
- Required controller firmware protocol: **v2.24** (firmware v2.25 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
