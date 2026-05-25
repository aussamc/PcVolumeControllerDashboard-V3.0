# Release Notes — v2.51

## Feature: Media key button actions

Each encoder button's short press, long press, and double press action can now be
set to a media key command:

| Action | Key sent |
|--------|----------|
| Play / Pause | `VK_MEDIA_PLAY_PAUSE` (0xB3) |
| Next track | `VK_MEDIA_NEXT_TRACK` (0xB0) |
| Previous track | `VK_MEDIA_PREV_TRACK` (0xB1) |
| Stop | `VK_MEDIA_STOP` (0xB2) |

### What changed

- Added `MediaPlayPause`, `MediaNextTrack`, `MediaPrevTrack`, `MediaStop` constants
  to `ChannelButtonActions`.
- Added `keybd_event` P/Invoke with `KEYEVENTF_EXTENDEDKEY` flag (required for media
  virtual keys) and a `SendMediaKey(byte vk)` helper.
- Updated `ApplyShortButtonAction`, `ApplyLongButtonAction`, `ApplyDoubleButtonAction`
  to dispatch the four new actions.
- Extended all three button-action ComboBoxes in the Channel panel with the four new
  items (indices 8–11 for short press, 7–10 for long/double press).
- Updated `GetSelectedChannel*ActionFromUi`, `Get*ActionIndex`, and
  `GetButtonActionDisplayName` to round-trip the new actions correctly.

### Bug fixes included

- Fixed two unit tests (`Normalize_V4Settings_CreatesDefaultProfile` and
  `Normalize_UpToDateSettings_ReturnsFalse`) that still referenced settings schema
  version 5 after a v6 migration was added in an earlier release.  Tests now use
  version 6 as the current schema version throughout.

---

## Compatibility

- Dashboard: v2.51
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
