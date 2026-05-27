# Release Notes — v2.57

## Bug fixes

### Channel linking: linked channels now propagate from Master and Mic Input sources

Turning a Master-volume or Mic Input encoder now correctly propagates the
volume delta to all channels in the same link group. Previously, the
`ChangeChannelVolume` function returned early from the Master and Mic Input
branches before reaching the propagation block, silently breaking ganged
behaviour whenever the source channel was assigned to master volume or the
microphone.

### Settings migration: deep-copy of MuteHotkey and Presets in v4→v5

The v4→v5 profile-creation migration now constructs a new `HotkeyBinding`
and a new `VolumePreset[]` for each channel instead of copying the object
references. The previous shallow copy aliased the live channel and its
profile copy to the same objects, meaning a change to either would
silently mutate the other.

---

## UI fix

### Channel Mapping toolbar split into two rows

The Assign and Save Name buttons were clipped or hidden when the window
was at or near its minimum width (980 px) because the entire toolbar
totalled ~901 px in a ~490 px column.

The toolbar is now split into two rows:

- **Row 1** — Channel selector · Assign to (fluid width) · **Assign**
- **Row 2** — Display name (fluid width) · **Save Name**

Both rows fit at any window width and the target and display-name fields
now expand to fill the available space rather than having fixed pixel widths.

---

## Compatibility

- Dashboard: v2.57
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
