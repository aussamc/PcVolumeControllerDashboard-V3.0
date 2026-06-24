# v3.6 — Phase 1: Avalonia encoder feel + button actions

v3.6 brings the cross-platform Avalonia host to feel parity with the WPF host on
the rotary encoders and adds the per-channel button actions. The shipping
Windows (WPF) dashboard is unchanged.

## Encoder feel

- **Acceleration**: turning a knob faster takes bigger volume steps, using the
  Light / Medium / Aggressive presets or the custom threshold/multiplier/curve —
  the same `Core.EncoderMath` formulas the WPF host uses, driven by the Encoder
  Feel settings and honouring per-channel sensitivity overrides.
- **Volume smoothing**: when enabled, the volume eases toward its target over
  ~60 Hz EMA ticks (Fast / Normal / Slow) instead of snapping, working in
  normalised float space and clamped to each channel's volume limits. Falls back
  to a direct write when the channel is momentarily unavailable.

## Button actions

Short, long, and double press now each run the channel's configured action:

- **Toggle mute**, **No action**, and **Apply Preset 1/2/3** (presets honour
  smoothing) are fully wired.
- **Media keys** (play/pause, next, previous, stop) are sent on Windows.
- Profile cycling, output-device cycling, and select-next-channel are logged as
  not-yet-ported (they need subsystems that land with later tabs).

## Internals

- `Core.EncoderMath.StepFromSensitivity` extracted and unit-tested (+6 tests).
- `ChannelRuntime` rewritten with per-channel acceleration timing, an EMA
  smoothing timer, and button-action dispatch — all on the UI thread.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** or later (v2.25
current, backward-compatible).
