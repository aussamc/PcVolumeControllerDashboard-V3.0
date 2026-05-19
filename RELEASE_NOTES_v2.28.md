# Release Notes — v2.28

## Changes

### Per-channel encoder sensitivity
Each encoder channel can now have its own sensitivity setting, independent of the global
encoder sensitivity slider. The new control appears in the **Per-Channel Controls** section
of the Audio tab:

- **Use global** (default, checkbox checked) — the channel inherits the global sensitivity
  value set in the Audio Setup section. No behaviour change from earlier versions.
- **Custom** (checkbox unchecked) — a slider appears allowing a per-channel override in
  the range 0–500%. The value is stored in `ChannelSettings.SensitivityPercent`.

Per-channel sensitivity values are saved with each profile and restored on load.
`GetVolumeStepPercentForChannel()` now checks the per-channel override before falling
back to the global value, so acceleration still applies on top of whichever sensitivity
is active.

### Microphone / input volume control
The default Windows audio **capture endpoint** (microphone) is now available as an
assignable audio target. It appears in the **Channel Mappings** target list as
**"Microphone Input"** and uses the special key `MIC_INPUT`.

Assigning an encoder channel to Microphone Input lets the user control microphone
gain from the hardware knob exactly as they would any playback app:
- Turning the knob increases or decreases the capture endpoint's master volume.
- Pressing the knob (short press → toggle mute) mutes or unmutes the microphone.
- The OLED display and the volume bar in the channel list reflect the current mic
  volume in real time.

The capture device is refreshed whenever `RefreshDefaultAudioDevice()` runs (on
connect and on the periodic poll). If no capture device is present the target is
silently omitted from the list.

## Compatibility

- Dashboard: v2.28
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
