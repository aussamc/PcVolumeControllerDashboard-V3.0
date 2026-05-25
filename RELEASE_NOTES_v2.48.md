# Release Notes — v2.48

## VoiceMeeter audio backend

Adds a new **Audio Backend** setting that lets the dashboard route encoder volume/mute control through VoiceMeeter instead of the Windows WASAPI audio session manager.

### How it works

In **WASAPI mode** (the default), the dashboard works exactly as before — encoder knobs control Windows app audio sessions (per-process volume, master, microphone).

In **VoiceMeeter mode**, encoder knobs control VoiceMeeter strip and bus gains instead. The target list in the Audio tab is replaced with VoiceMeeter strips and buses (e.g. "Strip 1", "Bus 2"). Volume is expressed on VoiceMeeter's dB scale: 0 % = −60 dB, 100 % = +12 dB.

### Volume scale (VoiceMeeter mode)

| Knob position | dB value |
|---------------|----------|
| 0 % | −60 dB |
| ~83 % | 0 dB (unity gain) |
| 100 % | +12 dB |

### Setup

1. Install and start VoiceMeeter (Vanilla, Banana, or Potato).
2. Go to the **Setup** tab → **Audio Backend** → select **VoiceMeeter** → click **Apply Backend Change**.
3. Confirm the warning — all channel assignments in every profile are cleared (a backup is saved automatically).
4. Assign each encoder to a VoiceMeeter strip or bus on the Audio tab.

The backend selector also appears as **Step 3** in the First-Run Wizard.

### VoiceMeeter offline detection

If VoiceMeeter is not running while in VoiceMeeter mode, a warning banner is shown. The dashboard polls for VoiceMeeter every 2 seconds and reconnects automatically when it starts.

### DLL detection

The VoiceMeeter Remote API DLL (`VoicemeeterRemote64.dll`) is located via the Windows registry (`HKLM\SOFTWARE\VB-Audio\VoiceMeeter\InstallDir`). A fallback scan of the Windows uninstall keys is used if the primary key is absent. If VoiceMeeter is not installed, the VoiceMeeter option is inert.

### Settings migration

Settings version bumped from 5 → 6. `AudioBackendMode` field added (defaults to `"WASAPI"`). No action required for existing users.

---

## Compatibility

- Dashboard: v2.48
- Required firmware protocol: v2.24 (no reflash required)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
