# v3.4 — Phase 1: Avalonia Audio tab + live runtime

v3.4 brings the cross-platform Avalonia host to life: it connects to the
controller and the physical knobs/buttons drive audio, with a live Audio tab
showing channel state. The shipping Windows (WPF) dashboard is unchanged.

## Runtime backbone (now live)

- **Per-OS audio backend composition**: `App.Avalonia` is multi-targeted
  (`net10.0` + `net10.0-windows`); a factory selects `WasapiAudioBackend` /
  `VoiceMeeterBackend` on Windows and Core's `NullAudioBackend` elsewhere.
- **Serial connection lifecycle**: `Core.SerialProtocol` (pure, unit-tested
  parser) + `SerialConnectionService` (multi-port auto-connect scan + identity
  handshake) + a file logger.
- **Channel runtime**: encoder turns adjust the assigned channel's volume and a
  short press toggles mute, marshalled to the UI thread.

## Avalonia Audio tab

- **Live channel-mapping grid**: the six channels with display name, assigned
  target, volume, mute, and status, refreshed ~2×/second so the dashboard tracks
  the hardware.
- **Target assignment**: pick a channel, choose an available audio target, and
  assign (or clear) it.
- Connection status indicator.
- Per-channel detail (button actions, presets, link groups, pools, volume
  limits, per-channel OLED/sensitivity), profiles, output-device cycling, and
  connect/disconnect controls are ported in follow-up PRs.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** or later (v2.25
current, backward-compatible).
