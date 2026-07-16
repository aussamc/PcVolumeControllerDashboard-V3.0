# v3.22.1 — Controller input wakes the displays

Fixes a bug where the OLED displays could stay dark while the knobs still worked.
App-only; no firmware change or reflash.

## The bug

Auto sleep/wake blanks the controller's OLEDs after the PC has been idle for 10
minutes (`PC_IDLE`) and suppresses display updates until the PC is active again. But
"PC active" was measured only from the Windows idle timer (`GetLastInputInfo`), which
sees **mouse/keyboard input only**. Encoder turns arrive over USB serial, so they never
reset that timer. If you came back and used **only the controller knobs**, the PC kept
thinking it was idle: volume changed (the controls worked) but the OLEDs stayed blank
and were never told to wake.

## The fix

The host now treats **any physical controller input** — a knob turn, a button press, or
the controller's own local-wake notification — as activity. Receiving one while the
controller is asleep sends `WAKE` and repaints the OLEDs from live state, regardless of
what the OS idle timer thinks. Keepalive traffic (`PONG`) is correctly ignored, so
auto-sleep still works when the controller really is idle.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
