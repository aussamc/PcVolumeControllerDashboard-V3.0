# Release Notes — v2.19

## Changes

### Custom acceleration fine-tuning

A new **Custom** option has been added to the Acceleration preset selector in the
Setup tab → Encoder Feel section.

When **Custom** is selected, a tuning panel appears with three sliders:

| Slider | Range | Default | Effect |
|--------|-------|---------|--------|
| Speed threshold | 20–250 ms | 120 ms | Turn interval below which acceleration kicks in. Higher values make acceleration engage earlier. |
| Max multiplier | 1.5×–8.0× | 4.0× | Maximum step-size boost applied when turning at full speed. |
| Curve shape | 0.3–2.5 | 0.7 (Soft) | Controls how quickly the multiplier builds up. Values below 1 reach near-max boost early (Early/Soft); 1.0 is a linear ramp; values above 1 keep the multiplier low until near-maximum speed (Late/Sharp). |

A live preview row beneath the sliders shows the computed multiplier at four
representative turning speeds (idle, medium, fast, and maximum) so you can see
exactly how the curve behaves before turning the encoder.

The acceleration formula is:

```
speedFactor  = clamp((threshold - interval) / threshold, 0, 1)
curved       = speedFactor ^ curveExponent
multiplier   = 1 + (maxMultiplier - 1) × curved
```

**Motivation:** The built-in Light / Medium / Aggressive presets are fixed and may not
suit every user's preferred feel. This panel lets you dial in the exact response you
want — for example, a high threshold with a soft curve gives broad acceleration across
a wide speed range, while a low threshold with a sharp curve gives a quick burst only
at the very fastest turns.

## Compatibility

- Dashboard: v2.19
- Required firmware protocol: v2.15 (no firmware change — no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
