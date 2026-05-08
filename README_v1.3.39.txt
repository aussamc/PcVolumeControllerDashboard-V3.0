PC Volume Controller Dashboard v1.3.39

Version summary
- Dashboard: v1.3.39
- Required ESP32 protocol: v1.3.38 Beta 7
- Included ESP32 firmware source: Computer_Volume_Controller_v1.3.38_Beta_7

What changed in v1.3.39
- Stopped using the beta naming scheme for new dashboard releases.
- Added per-channel encoder sensitivity controls in the Selected Channel panel.
- Added per-channel short-button action options:
  - Select next channel
  - Toggle assigned mute
  - No action
- Hardware encoder rotation now applies the sensitivity setting for that physical channel.
- Kept the v1.3.38 Beta 8 connection-stability behaviour.
- Kept the release-folder cleanup rule: only current notes remain in the main folder; older notes are stored in previous_version_notes/.

Firmware note
- This is a dashboard-only release.
- No ESP32 reflash is required if the controller already has the v1.3.38 Beta 7 firmware installed.
