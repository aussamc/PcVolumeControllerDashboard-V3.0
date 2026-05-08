PC Volume Controller Dashboard v1.3.33

Major features:
- Firmware Update tab scaffold for future ESP32 flashing.
- Hardware Test tab for encoder/button counters and display test commands.
- Debug tab for serial protocol console with optional heartbeat display.
- Crash log handling via crash-YYYYMMDD-HHMMSS.log files in AppData logs.
- Safe mode argument: --safe disables auto-connect/reconnect and skips audio-control writes.
- Setup diagnostics tools: open current log, copy log folder path, export diagnostics zip, clear remembered controller, factory reset setup.

ESP32 firmware:
- Firmware project renamed to Computer_Volume_Controller_v1.3.33 because this release adds a real firmware command: TEST_DISPLAY.
- The release zip includes Arduino .ino source.
- Compiled .bin files could not be generated in this environment because arduino-cli is unavailable; see firmware_bin/build_instructions.txt.
