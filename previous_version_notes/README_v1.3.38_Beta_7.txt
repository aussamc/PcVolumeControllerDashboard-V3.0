PC Volume Controller Dashboard v1.3.38 Beta 7

Current build notes
- Default connected OLED idle action is now Dim brightness to 30%.
- Connected idle action dropdown now supports Display off and Dim brightness to 10%, 20%, 30%, 40%, 50%, 60%, or 70%.
- Dashboard sends the selected dim percentage to the controller firmware using DIM_10 through DIM_70.
- ESP32 firmware now accepts DIM_10 through DIM_70 and applies the selected idle dim level.
- Fixed the Encoder sensitivity slider scale so the 500% label aligns with the end of the shorter slider.
- Cleaned the release package layout: older build/readme/release-note files are in previous_version_notes/.

Version status
- Dashboard: v1.3.38 Beta 7
- Required ESP32 protocol: v1.3.38 Beta 7
- Firmware folder: Computer_Volume_Controller_v1.3.38_Beta_7

Important
Flash the included Beta 7 ESP32 firmware to use the new connected idle dim-percent options.
