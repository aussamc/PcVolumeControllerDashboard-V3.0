# PC Volume Controller Dashboard v1.3.38 Beta 7

## Version status

Dashboard version: v1.3.38 Beta 7  
Required ESP32 protocol: v1.3.38 Beta 7  
ESP32 firmware folder: `Computer_Volume_Controller_v1.3.38_Beta_7`

## Changes

1. Connected OLED idle default changed to **Dim brightness to 30%**.
2. Connected idle action dropdown now includes:
   - Display off
   - Dim brightness to 10%
   - Dim brightness to 20%
   - Dim brightness to 30%
   - Dim brightness to 40%
   - Dim brightness to 50%
   - Dim brightness to 60%
   - Dim brightness to 70%
3. ESP32 OLED config protocol now accepts `DIM_10` through `DIM_70` for connected idle dim actions.
4. Fixed the Encoder sensitivity slider scale so **500%** is aligned with the actual end of the shorter slider.
5. Release package cleanup: older build notes are moved to `previous_version_notes/`; only current Beta 7 notes remain in the project root.

## Firmware note

This version changes ESP32 firmware behaviour. Flash the included Beta 7 firmware before testing the new idle dim percentage options.
