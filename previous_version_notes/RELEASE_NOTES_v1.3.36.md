# PC Volume Controller Dashboard v1.3.36

## Added

- First Run tab / setup wizard.
- Controller detection step using the existing strict ESP32 identity handshake.
- Startup setup choices for auto-connect, start with Windows, minimize to tray, and scan-all COM-port behaviour.
- Apply Recommended wizard action for daily-use setup.
- Mark Wizard Complete setting so the wizard does not have to be the main focus after setup.

## Unchanged

- Required ESP32 protocol remains v1.3.33+.
- ESP32 firmware project remains `Computer_Volume_Controller_v1.3.33`.
- `tools/esptool.exe` remains bundled.
