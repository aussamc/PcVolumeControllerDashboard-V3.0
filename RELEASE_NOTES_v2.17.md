# Release Notes — v2.17

## Changes

### Volume smoothing rewrite — analog potentiometer feel

The smoothing engine has been completely rewritten based on analysis of the v2.16 diagnostics
log. Four root causes of the "sporadic and inaccurate" feel were identified and fixed:

**1. Integer quantisation eliminated**
The pipeline now operates entirely in normalized float space (0.0–1.0), matching the WASAPI
`ISimpleAudioVolume` API natively. Previously, intermediate steps were rounded to integer
percent, which caused most of the animation frames to produce no audible change followed by a
sudden large jump — destroying the smoothing effect for steps ≥ 4 %.

**2. True EMA convergence (no fixed tick count)**
The old implementation hard-stopped after exactly 5 ticks regardless of whether the target
was reached. The new implementation uses an Exponential Moving Average (EMA) step each tick:

```
next = current + alpha × (target − current)
```

The timer continues firing until `|target − current| < 0.2 %` and then snaps to the exact
target. Fast encoder turns simply move the target further out; the animation continues without
interruption.

| Speed | Alpha | ~97 % converged |
|-------|-------|-----------------|
| Fast   | 0.50 | 5 ticks × 16 ms ≈ 80 ms  |
| Normal | 0.35 | 8 ticks × 16 ms ≈ 128 ms |
| Slow   | 0.22 | 13 ticks × 16 ms ≈ 208 ms |

**3. Windows multimedia timer resolution raised to 1 ms**
`timeBeginPeriod(1)` is now called at startup (paired with `timeEndPeriod(1)` at shutdown).
Without this, Windows' default 15.6 ms timer quantum caused smoothing ticks to randomly slip
by a full quantum, collapsing two ticks into one ~72 ms gap and creating a visible stutter.
With `timeBeginPeriod(1)` active, the 16 ms timer fires reliably at ±1 ms.

**4. Target re-base bug fixed**
When a new encoder event arrived while smoothing was already active, the code re-read the
actual (un-interpolated) audio volume as the base for the new target. Because the interpolation
hadn't finished yet, this made the target snap back toward the current position and then surge
forward, producing a lurching motion during fast sweeps. The fix: when smoothing is active,
new encoder events extend `_smoothingTargetVolumes` from its current value — never from the
audio API.

### Short press default restored to Toggle Mute (settings migration)

A v3 → v4 `NormalizeSettings` migration sets any channel whose short press was `NoAction`
back to `ToggleAssignedMute`. This corrects existing users who were moved to `NoAction` by
the earlier v2 → v3 migration and should now have mute-on-short-press as the default.

## Compatibility

- Dashboard: v2.17
- Required firmware protocol: v2.15 (no firmware change — no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
