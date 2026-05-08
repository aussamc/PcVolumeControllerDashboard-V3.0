PC Volume Controller Dashboard v1.3.38 Beta 8

Version summary
- Dashboard: v1.3.38 Beta 8
- Required ESP32 protocol: v1.3.38 Beta 7
- Included ESP32 firmware source: Computer_Volume_Controller_v1.3.38_Beta_7

What changed in Beta 8
- Dashboard-only connection stability polish.
- The dashboard no longer clears a remembered controller COM-port open-fail cooldown immediately on any Windows device-change event.
- The remembered controller port cooldown is now cleared only after SerialPort.GetPortNames() confirms the remembered port is present again.
- After a remembered controller port reappears, the dashboard waits a short USB settle delay before opening it.
- If the remembered controller port is temporarily missing, the dashboard waits before falling back to scanning unrelated COM ports such as COM1.
- The dashboard now skips opening a COM port that Windows is not currently listing, avoiding misleading "Could not find file 'COMx'" errors during USB re-enumeration.
- Existing DTR/RTS disabled behaviour from Beta 7 connectfix is kept.

Firmware notes
- No firmware change in Beta 8.
- No ESP32 reflash is required if you are already running Beta 7 firmware.

Package notes
- Only this Beta 8 README and release notes remain in the main folder.
- Older notes are stored in previous_version_notes/.
