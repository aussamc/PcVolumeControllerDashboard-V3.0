# PC Volume Controller Dashboard v1.3.38 Beta 7 connectfix

## Summary

This is a dashboard-only connect/disconnect stability fix for Beta 7.

## Diagnostic findings

The uploaded logs showed several related symptoms:

1. The dashboard connected successfully to COM9 and received the expected Beta 7 HELLO.
2. During repeated connect/disconnect testing, COM9 briefly disappeared and the dashboard logged `Could not find file 'COM9'`.
3. The serial stream showed ESP32-S3 boot/reset text including `USB_UART_CHIP_RESET`, which strongly points to USB CDC control-line reset during reconnect cycles.
4. Manual disconnect lockout was logged, but queued/alternate reconnect paths could still reopen the serial port soon afterwards.

## Changes made

- Kept serial DTR and RTS disabled when opening the COM port.
- Added one shared manual-disconnect path for dashboard button and tray disconnect.
- Added an explicit guard inside auto-reconnect connection attempts so queued auto-connects cannot bypass manual disconnect lockout.
- Increased auto-reconnect cooldown from 1.5 seconds to 3 seconds.
- Increased Windows device-change debounce from 1.5 seconds to 2.2 seconds.
- Added clearer logs when auto-connect is suppressed by manual disconnect lockout.

## Version status

Dashboard version:       v1.3.38 Beta 7 connectfix
Required ESP32 protocol: v1.3.38 Beta 7
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_7

## Firmware note

No ESP32 firmware change is required for this build. Existing Beta 7 firmware remains correct.
