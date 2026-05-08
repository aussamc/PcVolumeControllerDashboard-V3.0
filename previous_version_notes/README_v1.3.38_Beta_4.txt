PC Volume Controller Dashboard v1.3.38 Beta 4

Build focus:
- Adds a Save OLED Setup button at the bottom of the OLED Setup tab.
- Sends OLED settings to the connected ESP32 using OLEDCFG when saved.
- Updates the ESP32 firmware source to understand OLED display mode, brightness, and dim/sleep timeout settings.

Version status:
- Dashboard: v1.3.38 Beta 4
- Required ESP32 protocol: v1.3.38 Beta 4
- Included ESP32 firmware source: Computer_Volume_Controller_v1.3.38_Beta_4

Important:
- Because this beta changes ESP32 firmware behavior, flash the included v1.3.38 Beta 4 firmware before expecting the physical OLED display to react to OLED setup changes.
- No compiled .bin files are included from this environment; use the firmware build instructions in firmware_bin/build_instructions.txt.
