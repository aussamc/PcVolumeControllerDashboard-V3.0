# Release Notes — v2.16

## Changes

### Volume acceleration
A new **Encoder Feel** section has been added to the Setup tab.

**Volume acceleration** scales the volume step size when you turn an encoder quickly — slow turns still produce fine adjustments while fast sweeps cover more ground. Three presets are available:

| Preset | Behaviour |
|--------|-----------|
| Light | 2× step when interval < 80 ms |
| Medium | 2× below 100 ms, 3× below 60 ms |
| Aggressive | 2× below 110 ms, 3× below 70 ms, 4× below 50 ms |

Acceleration is off by default. Enable it with **Enable volume acceleration** and choose a preset.

### Volume smoothing
**Volume smoothing** eases the actual audio session volume toward its target over a short time, giving knob turns a premium, analog feel instead of instant jumps. Three speed presets are available:

| Speed | Approximate convergence |
|-------|------------------------|
| Fast | ~60 ms |
| Normal | ~100 ms |
| Slow | ~150 ms |

Smoothing uses an ease-out curve — the volume moves quickly at first, then decelerates as it approaches the target. Rapid encoder turns re-aim the target without restarting the animation. Mute/unmute always applies instantly regardless of the smoothing setting.

Smoothing is off by default. Enable it with **Enable volume smoothing** and choose a speed.

Acceleration and smoothing are independent toggles and can be used together.

### Per-channel button action alignment fix
The **Short press**, **Long press**, and **Double press** dropdown labels in the Per-Channel Controls panel now use a fixed-width label column so all three ComboBoxes align at the same horizontal position.

## Compatibility

- Dashboard: v2.16
- Required firmware protocol: v2.15 (no firmware change — no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
