# Full version compatibility

Complete dashboard → firmware → hardware compatibility history. The
[README](README.md#version-compatibility) shows only the most recent releases; this
file is the full archive.

## How to read this

Two different numbers matter, and they are not the same thing:

- **Minimum protocol** — the oldest firmware the dashboard will *handshake* with. The
  handshake is strict: a controller reporting a protocol below this is rejected and
  never connects. This has sat at **v2.24** since dashboard v2.24 because the wire
  protocol itself (`STATE`/`CHSTATE`/`OLEDCFG`/`DISPMODE`/`PING`/`SLEEP`/`WAKE`,
  `ENC`/`BTN_*`/`PONG`) has not changed since then.
- **Matching firmware** — the firmware that ships the *feature set* that dashboard
  release was built and tested against. Anything between "minimum" and "matching"
  connects and works, but one or more features are missing or degraded on the
  controller side (see the firmware ladder below for what you lose).

Rule of thumb: **flash the matching firmware.** The minimum column only tells you
whether an old controller will still talk at all.

Only the current firmware source (`Computer_Volume_Controller_v2.31/`) is kept in the
repo; older firmware is available from git history at the tag/commit that introduced it.

## Firmware ladder — what each version added

| Firmware | Added | Missing if you stay on older firmware |
|---|---|---|
| v2.31 | 2-D software anti-burn-in jitter (drawing origin walks a 3×3 grid, clipped not wrapped) | Dashboard's OLED anti-burn-in preview no longer matches the real screen; the older hardware-shift approach can wrap content |
| v2.30 | Unified "Disconnected" + firmware-version screen across all display modes | Per-display-mode disconnected layouts (inconsistent, no firmware version shown) |
| v2.29 | Non-blocking serial TX, loop-task watchdog, 3-min default no-dashboard sleep | Sleep countdown can be starved if the PC shuts down with the dashboard still connected — controller stays awake |
| v2.28 | OLED anti-burn-in shift (hardware `SETDISPLAYOFFSET`) | No burn-in mitigation |
| v2.27 | Non-blocking boot firmware-version splash | No on-device firmware version readout at boot |
| v2.26 | Large Volume OLED redesign (bigger name/number, MUTE indicator) | Large Volume display mode renders in the old, smaller layout |
| v2.25 | Chip ID as 5th `HELLO` field | "Paired controller" always shows "(none)" — controller pairing cannot work |
| v2.24 | Per-channel OLED display modes (`DISPMODE`) — **wire-protocol baseline** | Rejected at handshake by every dashboard from v2.24 onward |

## Compatibility matrix

| Dashboard | Minimum protocol | Matching firmware | Hardware |
|---|---|---|---|
| v3.23 – v3.23.5 | v2.24 | **v2.31** | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.22.5 | v2.24 | v2.30 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.20 – v3.22.4 | v2.24 | v2.28 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.19.2 – v3.19.6 | v2.24 | v2.27 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v3.16 – v3.19.1 | v2.24 | v2.26 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.58 – v3.15 | v2.24 | v2.25 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.24 – v2.57 | v2.24 | v2.24 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.21 – v2.23 | v2.21 | v2.21 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.15 – v2.20 | v2.15 | v2.15 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.12 – v2.14 | v2.12 | v2.12 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.10 – v2.11 | v2.10 | v2.10 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.9 | v2.9 | v2.9 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.5 – v2.8 | v2.5 | v2.5 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v2.0 – v2.4 | v2.0 | v2.0 | v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8) |
| v1.3.39 | v1.3.38 Beta 7 | v1.3.38 Beta 7 | Prototype (1-channel) |

Dashboard releases within a range differ only in host-side changes — they were all
built against the same controller firmware. For releases at or before v2.23 the repo
has no firmware-feature history beyond the protocol number, so minimum and matching
are recorded as the same value.

## Maintaining this file

On a firmware bump: add a row to the firmware ladder, and start a new matrix row for
the dashboard version that ships alongside it. On a dashboard bump with no firmware
change: extend the top matrix row's range rather than adding a row.
