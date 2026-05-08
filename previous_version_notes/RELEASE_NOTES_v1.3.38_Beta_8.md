# PC Volume Controller Dashboard v1.3.38 Beta 8

## Version status

Dashboard version:       v1.3.38 Beta 8
Required ESP32 protocol: v1.3.38 Beta 7
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_7

## Main goal

Beta 8 is a dashboard-only connection stability and logging polish release based on the diagnostics collected after Beta 7 connectfix.

## Changes made

1. Remembered controller port handling is safer.
   - The dashboard no longer clears a phantom/open-fail cooldown immediately on a Windows device-change event.
   - It now waits until SerialPort.GetPortNames() confirms the remembered controller port is actually present again.

2. Added a USB settle delay before reopening the remembered controller port.
   - This is intended to avoid opening COM9 while Windows is still recreating the ESP32-S3 USB CDC port.

3. Reduced fallback probing of unrelated COM ports.
   - If a remembered controller port exists but is temporarily missing, the dashboard waits before using scan-all fallback.
   - This avoids wasting time probing COM1 during short Windows USB/COM churn.

4. Added pre-open COM-port presence check.
   - The dashboard now skips opening a port that Windows is not currently listing.
   - This should reduce misleading "Could not find file 'COMx'" errors.

5. Kept Beta 7 connectfix behaviour.
   - DTR and RTS remain disabled when opening the serial port.
   - Manual disconnect lockout behaviour is preserved.

## Firmware impact

No firmware change.
No ESP32 reflash is required if the controller already has the Beta 7 firmware.

## Package layout

Older README and release-note files are kept in `previous_version_notes/`.
Only the current Beta 8 notes remain in the main project folder.
