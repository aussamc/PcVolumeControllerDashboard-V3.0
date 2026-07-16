# v3.22.2 — Fix encoder/OLED lag that grew with PC uptime

Fixes the dashboard getting progressively slower to respond the longer the PC had
been on: encoder turns took seconds to move the volume, and the OLEDs and on-screen
overlay lagged behind. App-only; no firmware change or reflash.

## The bug

Windows keeps an entry in the WASAPI session list for **every process that has ever
played audio** since the audio engine started — these "expired" sessions are never
removed while the PC stays up. The WASAPI backend re-enumerated that ever-growing
list on essentially every volume/mute operation (the session cache lasted only
100 ms), and for **each** session on **each** call it looked the owning process up
with `Process.GetProcessById` — which *throws* for the dead PIDs behind expired
sessions. Hours into a session (browsers spawn many audio sessions), every encoder
step was paying for hundreds of swallowed exceptions, several times over, all on the
UI thread. Measured in the logs: a queued encoder step cost ~60 ms after 1 hour of
uptime and ~330 ms after 6 hours — so a quick knob turn took 8+ seconds to finish
applying, and the OLED/overlay update (which waits on the audio write) arrived last.

## The fix

- **Expired sessions are skipped at enumeration time**, so the dead-session backlog
  costs nothing downstream and the per-operation cost stays flat over uptime.
- **Process names are resolved once per session-cache refresh** (one lookup per
  unique PID, dead PIDs remembered for the refresh) instead of per session per
  call — no more exception storms on the hot path.
- **The session cache TTL was raised from 100 ms to 500 ms**, so the 20 Hz UI poll
  and encoder bursts stop forcing a full multi-endpoint re-enumeration ten times a
  second. New audio sessions still appear within half a second.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
