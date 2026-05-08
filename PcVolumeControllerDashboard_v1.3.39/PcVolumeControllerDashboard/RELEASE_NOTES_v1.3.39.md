# PC Volume Controller Dashboard v1.3.39

## Version status

Dashboard version:       v1.3.39
Required ESP32 protocol: v1.3.38 Beta 7
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_7

## Changes

1. Removed beta naming for new dashboard releases.
   - New release package uses `v1.3.39` rather than `Beta` naming.

2. Added per-channel encoder sensitivity.
   - The Selected Channel panel now includes a per-channel sensitivity slider.
   - Each physical encoder/channel can now have its own sensitivity setting.
   - Encoder rotation now uses the sensitivity configured for that specific channel.

3. Added per-channel short-button actions.
   - Select next channel.
   - Toggle assigned mute.
   - No action.

4. Kept the v1.3.38 Beta 8 connection-stability improvements.
   - DTR/RTS remain disabled.
   - Remembered-port stability logic remains in place.
   - COM-port fallback behaviour remains conservative.

5. Release folder cleanup remains active.
   - Older README/release-note files are stored under `previous_version_notes/`.
   - Only the current v1.3.39 notes remain in the main project folder.

## Firmware impact

No ESP32 firmware change was made for this release.
No ESP32 reflash is required if the controller already has the v1.3.38 Beta 7 firmware installed.
