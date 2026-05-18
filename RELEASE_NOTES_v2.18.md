# Release Notes — v2.18

## Changes

### Volume smoothing — dashboard and OLED preview now animate in sync

When volume smoothing is enabled, the channel list volume bars, the selected channel
volume display, and the OLED preview panels now animate frame-by-frame alongside the
actual audio volume instead of waiting for the 500 ms background poll to catch up.

**Root cause:** `RefreshAllChannelStates()` — called after every smoothing tick — reads
`AudioTargetItem.Volume`, a cached integer that is only refreshed by `StatePollTick`
every 500 ms. The smooth intermediate values written to WASAPI by `SetChannelVolumeAbsolute`
were never reflected in `AudioTargetItem.Volume`, so the UI displayed the old value for up
to 500 ms and then snapped to the final value in one jump.

**Fix:** `SmoothingTick` no longer calls `RefreshAllChannelStates()`. Instead, it updates
`_channels[ch].Volume` directly from `_smoothingCurrentVolumes[ch]` (the live float tracked
inside the smoothing engine) and then calls the three UI refresh methods individually:

```
ChannelMappingsListView.Items.Refresh()   → animates the channel list volume bars
UpdateSelectedChannelUi()                 → animates the selected channel detail panel
UpdateOledPreviewPanels()                 → animates the OLED simulation panels
```

The result: every ~16 ms tick the dashboard and OLED preview advance one EMA step in
perfect sync with what is being written to the audio session — giving the full
analog-feel animation end-to-end.

## Compatibility

- Dashboard: v2.18
- Required firmware protocol: v2.15 (no firmware change — no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
