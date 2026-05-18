# Release Notes — v2.22

## Changes

### Restore 25 ms encoder coalesce; encoder debounce diagnostic reverted to off

Hardware analysis in v2.20/v2.21 confirmed zero electrical bounce on all six encoders.
The only observed artefact was mechanical double-click: a second event arriving ~19–33 ms
after the first on the same encoder, most pronounced on Encoder 2 (~21% of events).

A 25 ms coalesce window is restored to absorb these mechanical doubles cleanly. The
previous 55 ms window was unnecessarily wide and delayed fast-turn acceleration
measurements; 25 ms sits just above the worst observed double-click (33 ms) while being
tight enough that normal fast turns are not coalesced.

The reverse-guard (140 ms) and reverse-confirm (2 events) settings are unchanged — all
observed direction changes were genuine (≥208 ms), so no relaxation is needed.

`EncoderDebounceDisabled` is reverted to `false`. The diagnostic mode remains in the
codebase as an instant flip-back option if further hardware analysis is needed.

| Constant | Was (v2.21) | Now (v2.22) |
|----------|-------------|-------------|
| `EncoderDebounceDisabled` | `true` | `false` |
| `EncoderApplyIntervalMs` | 55 ms | 25 ms |
| `EncoderReverseGuardMs` | 140 ms | 140 ms (unchanged) |

### Fix acceleration `isFirstEvent` always-false bug

The variable `isFirstEvent` was evaluated **after** `_accelPrevApplyAt[ch]` was updated,
so it always returned `false`. The first encoder event on each channel was incorrectly
treated as a continuation of a previous sequence instead of as a fresh start (interval =
`double.MaxValue`, no acceleration on first turn). The check is now evaluated **before**
the timestamp assignment.

### New default acceleration settings (Custom preset)

| Setting | Old default | New default |
|---------|-------------|-------------|
| Speed threshold | 120 ms | 150 ms |
| Max multiplier | 4.0× | 8.0× |
| Curve exponent | 0.7 (Soft) | 0.5 (Early) |

These defaults were validated against the raw hardware logs and provide stronger,
earlier-onset acceleration that better matches the physical feel of fast turns.

## No firmware change

No protocol change. No reflash required.

## Compatibility

- Dashboard: v2.22
- Required firmware protocol: v2.21
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
