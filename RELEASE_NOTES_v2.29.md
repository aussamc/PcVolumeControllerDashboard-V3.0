# Release Notes — v2.29

## Changes

### "Next Profile" hardware button action
A new button action **"Next profile"** is now available for short press, long press, and
double press on any encoder channel. When triggered it cycles to the next profile in the
profile list, wrapping around from the last profile back to the first.

This allows switching between channel-assignment presets entirely from the hardware
controller without touching the dashboard window.

### Global system hotkeys
A new **Global Hotkeys** section has been added to the **Setup** tab. Five independent
hotkeys can be assigned — all are unassigned by default:

| Action | Description |
|---|---|
| Master volume up | Raise the Windows master volume by one step |
| Master volume down | Lower the Windows master volume by one step |
| Toggle master mute | Mute or unmute the Windows master audio output |
| Next profile | Cycle to the next saved profile (same as the button action above) |
| Show dashboard | Bring the dashboard window to the foreground |

**How to assign a hotkey:**
1. Click **Set** next to the action.
2. In the dialog that appears, hold any combination of Ctrl, Alt, Shift, and/or Win,
   then press any non-modifier key (e.g. Ctrl+Shift+M).
3. Click **OK** to save, **Clear** to remove the binding, or **Cancel** to discard.

Hotkeys are registered as Win32 global hotkeys (`RegisterHotKey`) and work even when the
dashboard window is minimised or behind other windows. If another application has already
claimed the same combination the assignment is silently ignored — the label remains the
requested combination and will take effect if the conflicting application is closed.

Hotkey bindings are persisted in `settings.json` alongside all other settings.

## Compatibility

- Dashboard: v2.29
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
