# PC Volume Controller Dashboard v1.3.38 Beta 4 buildfix

This buildfix addresses the controller preview styling on the OLED Setup tab.

## Version status

Dashboard version:       v1.3.38 Beta 4 buildfix
Required ESP32 protocol: v1.3.38 Beta 4
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_4

## Changes made

1. Fixed the whole-controller preview background on the OLED Setup page.
   - Removed the hard-coded bright preview surface.
   - The preview surface now follows the active dashboard theme.

2. Made the encoder preview artwork theme-aware.
   - Encoder fills, outlines, and markings now adapt to the current theme.
   - The preview looks more consistent in dark mode and no longer appears washed out.

3. Kept firmware compatibility unchanged.
   - This buildfix does not change the ESP32 protocol.
   - Existing Beta 4 firmware is still the correct firmware to use.

## Notes

- This is a dashboard-only UI/theme fix.
- No firmware reflashing is required if you are already on Beta 4 firmware.
