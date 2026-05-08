PC Volume Controller Dashboard v1.3.37.1

Bug-fix patch focused on the ESP32 local no-dashboard sleep behaviour.

Changes:
- Dashboard version updated to v1.3.37.1.
- Required ESP32 protocol updated to v1.3.37.1.
- ESP32 firmware project renamed to Computer_Volume_Controller_v1.3.37.1 because this patch includes a real firmware logic change.
- ESP32 no-dashboard local sleep now starts counting from boot, even if the dashboard never connects.
- No-dashboard local sleep timeout changed to 2 minutes.
- Encoder turn or button press while locally asleep wakes the display and restarts the 2-minute idle countdown.
- First input while sleeping wakes only and does not send a volume/button action.
- Local activity now resets the no-dashboard idle countdown.
- tools/esptool.exe is included for dashboard firmware flashing.

Build notes:
- This package includes the ESP32 .ino source.
- ESP32 .bin files are not included because Arduino CLI / ESP32 core are not available in the current build environment.
- See firmware_bin/build_instructions.txt for local ESP32 build guidance.
