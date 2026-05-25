# Release Notes — v2.56

## Feature: Channel linking (ganged volume control)

Channels can now be placed in a named **link group**. When an encoder moves a
channel that belongs to a group, every other channel in the same group receives
the same volume delta at the same time — exactly like a ganged potentiometer.

### Usage

1. Select a channel in the **Audio** tab.
2. Scroll to the new **Channel Linking** section (below Volume Presets).
3. Type a group name (e.g. `A`, `music`, `left`) in the **Link group** field and
   press Tab or click away.
4. Repeat for any other channels that should move together.
5. Press **Clear** (or delete the text) to remove a channel from its group.

Channels in the same group:
- Move together when a physical encoder is turned.
- Move together when the **Volume +** / **Volume −** buttons are clicked in the UI.
- Each channel still obeys its own **Volume Limits** clamp independently.
- Mute state is independent — muting one channel does not affect the others.

### Technical details

- `ChannelSettings.LinkedGroupId` (string, default `""`) — the persistence field.
  Serialised in `settings.json`; empty string means not linked.
- `ChangeChannelVolume` accepts a `propagate` parameter (default `true`).
  Propagation calls linked channels with `propagate: false` to prevent recursion.
- The smoothing path in `ApplySmoothedEncoderDelta` propagates the same
  normalised delta to linked channels' smoothing targets so ganged channels
  animate in lockstep with volume smoothing enabled.
- `GetLinkedChannelIndices(sourceChannelIndex)` helper in `MainWindow.Encoder.cs`
  enumerates sibling channel indices from `_settings.Channels`.
- Settings migration (v4→v5 profile-creation block) updated to copy all
  `ChannelSettings` fields (including `LinkedGroupId`, `MinVolumePercent`,
  `MaxVolumePercent`, `MuteHotkey`, `Presets`) so migrating from very old
  settings never silently drops new fields.

---

## Compatibility

- Dashboard: v2.56
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
