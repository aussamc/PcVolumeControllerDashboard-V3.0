# Release Notes — v2.20

## Changes

### Encoder debounce disabled (diagnostic build)

All software debounce, coalescing, and reverse-guard logic has been **temporarily
bypassed** to allow raw hardware encoder behaviour to be observed and logged.

When `EncoderDebounceDisabled = true` (the current state):

- Every `ENC,<ch>,<delta>` event received from the firmware is applied immediately
  with no rate-limiting, no coalescing, and no reverse-direction filtering.
- Each raw event is written to the log with a `[RAW]` prefix containing:
  - Channel number
  - Raw delta value (+1 / −1)
  - Inter-event interval in milliseconds (or `first` for the first event on a channel)
  - `DIRECTION-CHANGE` tag when the event reverses the previous turn direction
- A `WARNING` banner is written at startup so logs are clearly flagged as diagnostic.

**This is not intended for normal use.** The flag will be reverted and normal
debounce/coalescing restored in the next version once the hardware analysis is complete.

### What to look for in the logs

| Pattern | What it means |
|---------|---------------|
| `DIRECTION-CHANGE` on a single step immediately after a turn | Hardware bounce — encoder generating a false step in the wrong direction |
| Two or more `[RAW]` events with interval < 5 ms | Electrical noise / contact chatter within a single detent |
| Interval spikes > 200 ms mid-turn | Firmware quadrature decode missing counts |
| Consistent single direction, smooth intervals | Clean hardware — debounce may be unnecessary |

## Compatibility

- Dashboard: v2.20
- Required firmware protocol: v2.15 (no firmware change — no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
