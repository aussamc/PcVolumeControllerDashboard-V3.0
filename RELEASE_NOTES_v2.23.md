# Release Notes — v2.23

## Changes

### App offline fallback — per-channel configurable

When a channel's assigned app is not running, you can now choose what the channel does.
The setting is per-channel and is configured in the new **Auto-Rebind** tab.

| Option | Behaviour |
|--------|-----------|
| **Show Inactive** (default) | Channel row is greyed out and italicised in the channel list. OLED displays "App offline" on the status line. As soon as the app opens again the channel becomes active automatically. |
| **Do Nothing** | Channel is silently inactive — no visual change, no OLED status update. |

Auto-rebind itself has always worked via process-name tracking (`PROC:<name>` keys): when
the app reopens, the audio session is found by name and the channel becomes active
immediately. This version adds the visual feedback layer on top of that existing behaviour.

### Greyed-out channel rows when app is offline

The channel list in the Audio tab now visually distinguishes offline channels:
rows for channels whose assigned app is not running are rendered at 45% opacity
with italic text (when the fallback is **Show Inactive**).

## No firmware change

No protocol change. No reflash required.

## Compatibility

- Dashboard: v2.23
- Required firmware protocol: v2.21
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
