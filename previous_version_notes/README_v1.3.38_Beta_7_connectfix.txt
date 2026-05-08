PC Volume Controller Dashboard v1.3.38 Beta 7 connectfix

Purpose
This build fixes connect/disconnect instability seen in the diagnostic logs after the Beta 7 buildfix.

Findings from diagnostics
- The controller was repeatedly moving between connected, disconnected, and identifying states.
- COM9 sometimes disappeared briefly and produced "Could not find file 'COM9'" during reconnect attempts.
- Serial boot text showed ESP32-S3 USB reset messages such as USB_UART_CHIP_RESET during repeated reconnect cycles.
- Manual disconnect lockout was active, but queued/repeated reconnect attempts could still occur in some paths shortly after disconnect.

Fixes
- Serial DTR and RTS are now kept disabled when opening COM ports to avoid resetting ESP32-S3 USB CDC boards during repeated connect/disconnect cycles.
- Manual disconnect now uses one shared lockout path for the button and tray menu.
- Queued auto-connect attempts are now ignored while manual disconnect lockout is active.
- Auto-reconnect cooldown increased from 1.5s to 3s.
- Device-change debounce increased from 1.5s to 2.2s to avoid reacting too early while Windows is recreating COM ports.

Version status
Dashboard version:       v1.3.38 Beta 7 connectfix
Required ESP32 protocol: v1.3.38 Beta 7
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_7

Firmware
No firmware change is required for this connectfix. If you already flashed Beta 7 firmware, do not reflash for this fix.
