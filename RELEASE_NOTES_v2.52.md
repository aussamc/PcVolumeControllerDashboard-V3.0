# Release Notes — v2.52

## Feature: Per-channel volume limits

Each channel now has a **Min volume** and **Max volume** setting (0–100 %).
Every encoder turn and preset application is clamped to this range — the volume
can never go below the minimum or above the maximum for that channel.

### Use-cases

- Cap a channel at 75 % so a loud application can never blast at full volume.
- Set a minimum of 20 % on a background-music channel so it always stays audible.
- Combine both to pin a channel to a narrow range.

### What changed

**Data model**

- `ChannelSettings` gains two new fields: `MinVolumePercent` (default 0) and
  `MaxVolumePercent` (default 100).
- Settings schema version bumped to **7**; the v6 → v7 migration validates and
  repairs any out-of-range or inverted pairs (swap min/max if min > max).

**Volume clamping**

- `ApplySmoothedEncoderDelta` (smoothing path): clamps the `_smoothingTargetVolumes`
  entry to `[limMin, limMax]` instead of `[0, 1]`.
- `ChangeChannelVolume` (direct path): clamps the computed `next` value for master,
  mic input, and WASAPI session targets.
- Helper `GetChannelVolumeLimitsNormalized(int)` added in `MainWindow.Encoder.cs`.

**Persistence**

- `SaveChannelsToSettings` preserves `MinVolumePercent` / `MaxVolumePercent` from
  the previous array (same pattern as `SensitivityPercent`).

**UI**

- "Volume Limits" section added to the channel settings panel (between Sensitivity
  and Volume Presets), containing two sliders labelled **Min volume** and
  **Max volume**.
- Sliders self-enforce min ≤ max: moving min above max pulls max up, and vice versa.

**Tests**

- Added `Normalize_V6Settings_ClampsInvalidVolumeLimits` unit test.
- Updated existing tests to reference schema version 7 as current.

---

## Compatibility

- Dashboard: v2.52
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
