# Release Notes — v2.21

## Changes

### All encoder software debounce removed — ESP32 firmware and dashboard

Both the firmware and the dashboard now have every layer of encoder debounce
stripped out so raw hardware behaviour is visible in the logs with nothing
in the way.

**Firmware (reflash required):**

| Filter removed | Was | Now |
|----------------|-----|-----|
| Transition time gate | `ENC_DEBOUNCE_US = 1000` (1 ms) | `0` (bypassed) |
| Report rate-limit | `ENC_REPORT_GUARD_MS = 3` (3 ms) | `0` (bypassed) |
| Invalid-quadrature accumulator reset | Cleared accumulator to 0 on `movement==0` | Skip only — accumulator preserved |
| Direction-reversal reset | Reset accumulator on mid-turn reversal | Always accumulate |

Button debounce (`BTN_DEBOUNCE_MS = 50`) is intentionally unchanged — it is
needed for correct button press detection and is unrelated to encoder analysis.

**Dashboard (already in v2.20):**
`EncoderDebounceDisabled = true` — bypasses all coalescing, rate-limiting, and
reverse-guard on the PC side. Every `ENC` event from the firmware is applied
immediately and logged with a `[RAW]` prefix.

**Protocol version bumped to v2.21 — reflash required.**

## Compatibility

- Dashboard: v2.21
- Required firmware protocol: v2.21 (**reflash required**)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
