# v3.22.5 — Controller sleeps 3 min after losing the dashboard (firmware v2.29)

Fixes the controller staying awake at full brightness forever when the PC is shut
down without closing the dashboard first. **Reflash to firmware v2.29 required for
the fix** (the dashboard change alone only updates the default timeout).

## The bug

The disconnected-sleep countdown has existed in the firmware since v2.24: lose the
dashboard, and after the configured timeout with no knob/button activity the OLEDs
sleep; any knob turn wakes them and restarts the countdown until the PC reconnects.
But when the PC shut down with USB standby power still feeding the board, it never
fired: the native USB CDC TX buffer fills once no host is draining it, `Serial`
writes block, and the main loop stalls before the countdown can hit — leaving the
displays lit at full brightness indefinitely.

## The fix (firmware v2.29)

- **Non-blocking serial TX** — a 0 ms TX timeout plus a larger TX ring buffer means
  a dead host makes writes drop instead of stalling the loop; a live host never
  fills the buffer at this protocol's message rates.
- **Loop-task watchdog (15 s)** — defense in depth: if the loop ever freezes for
  any other reason, the board reboots and the disconnected-sleep countdown then
  runs as designed.
- **Default no-dashboard sleep timeout is now 3 minutes** (was 2), applying from a
  cold boot before any PC has connected. The dashboard's OLEDCFG still overrides it
  (1–60 min, "Disconnected sleep timeout" on the OLED tab).

## Dashboard changes

- Default `OledSleepTimeoutMinutes` is now **3** (was 2) to match the firmware
  default. Existing saved settings keep their configured value.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged — v2.29's wire
  protocol is identical to v2.28; the reflash is only needed for the sleep fix).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
