# Release Notes — v2.53

## Feature: Per-channel mute hotkeys

Each channel now has its own **mute hotkey** — a global keyboard shortcut that
toggles mute for that specific channel regardless of which app has focus.

### What changed

**Data model**

- `ChannelSettings` gains a `MuteHotkey` property (type `HotkeyBinding`,
  defaults to unassigned).
- `SaveChannelsToSettings` preserves `MuteHotkey` from the previous channel
  array, consistent with how `SensitivityPercent` and `MinVolumePercent` are
  handled.

**Hotkey registration**

- Six new hotkey IDs added (`HotkeyIdChannelMuteBase + 0` … `+ 5`), allocated
  immediately after the five global hotkey IDs.
- All six are included in `AllHotkeyIds` so they are unregistered cleanly on
  every `RegisterAllHotkeys` call.
- `RegisterAllHotkeys` loops over `_settings.Channels` and registers each
  channel's `MuteHotkey` if assigned.

**Hotkey dispatch**

- `HandleHotkeyEvent` falls through to a `default` case that computes
  `channelIndex = id - HotkeyIdChannelMuteBase`; if the index is in range, it
  calls `ToggleChannelMute`, then refreshes channel state and sends to device.

**UI**

- A "Mute hotkey" row (label + **Set** / **Clear** buttons) is added to the
  channel settings panel, just above the Volume Limits section.
- The label is updated whenever a channel is selected (`UpdateChannelMuteHotkeyLabel`).
- `ChannelMuteHotkey_Set` opens the existing `HotkeyPickerDialog` with the
  channel name as the action label.
- `ChannelMuteHotkey_Clear` resets the binding and re-registers all hotkeys.

---

## Compatibility

- Dashboard: v2.53
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
