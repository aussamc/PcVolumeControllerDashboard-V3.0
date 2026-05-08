PC Volume Controller Dashboard v1.3.38 Beta 6

Version summary
- Dashboard: v1.3.38 Beta 6
- Required ESP32 protocol: v1.3.38 Beta 6
- Included ESP32 firmware source: Computer_Volume_Controller_v1.3.38_Beta_6

Important firmware note
- Beta 5 includes ESP32 firmware changes for OLED brightness/dimming behavior.
- Flash the included Beta 5 firmware if you want the physical OLED dimming fix.

Main changes
- Reworked OLED dimming to use a non-linear contrast curve instead of the previous direct 0-255 mapping.
- Added lower OLED drive settings for dim values using pre-charge and VCOMH commands.
- Brightness 0 now explicitly powers the OLED display off.
- Made OLED Setup and Setup sliders clickable.
- Shortened OLED Setup and Setup sliders so they no longer span the full page width.
- Added encoder highlight feedback in the OLED Setup controller preview when encoder input is received.

Build notes
- This package includes the updated .ino firmware source.
- Compiled .bin files are not included because this environment cannot compile Arduino ESP32 firmware.
