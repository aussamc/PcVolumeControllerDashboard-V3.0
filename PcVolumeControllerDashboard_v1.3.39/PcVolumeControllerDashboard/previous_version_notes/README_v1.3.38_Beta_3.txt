PC Volume Controller Dashboard v1.3.38 Beta 3

This beta continues the dashboard-side OLED preview/display-settings release.

Version:
- Dashboard: v1.3.38 Beta 3
- Required ESP32 protocol: v1.3.37.1
- Included ESP32 firmware source: Computer_Volume_Controller_v1.3.37.1

Beta 3 UI changes:
- Renamed the OLED preview tab to OLED Setup.
- Moved OLED Display Settings from Setup to OLED Setup.
- OLED previews now use a 128x64 canvas scaled to 256x128 on screen.
- OLED previews are arranged in order from OLED 1 through OLED 6.
- First Run wizard tab moved to the end of the tab list.

Firmware note:
- ESP32 firmware is unchanged in this beta.
- Do not rename the ESP32 firmware folder unless firmware behaviour changes.


Beta 3 UI changes:
- Reworked the OLED Setup tab into a whole controller preview.
- Kept OLED display settings at the top of the page.
- Shows all 6 OLEDs across the top with 6 encoder positions underneath.
