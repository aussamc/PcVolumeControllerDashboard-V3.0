# v3.2 — Phase 1: Avalonia port begins (audio seam + Setup tab)

v3.2 is the first milestone of **Phase 1**, the cross-platform Avalonia UI port.
It bundles the audio-backend extraction with the first user-facing Avalonia tab.
The shipping Windows (WPF) dashboard behaves exactly as before; the new
cross-platform `App.Avalonia` host now has a working Setup tab.

## Audio backend abstraction (the deferred Phase 0.4)

- New neutral seam in Core: `IAudioBackend` + `AudioTarget` (no Windows-only
  types leak through — backends own their handles and resolve targets by key).
- New `Platform.Windows` library with both Windows backends behind the seam:
  `WasapiAudioBackend` (WASAPI/NAudio) and `VoiceMeeterBackend`.
- The WPF host was rewired to drive all volume/mute/session work through the
  seam; the old `AudioService`/`VoiceMeeterService`/`AudioTargetItem` were
  removed. Hardware-verified (per-app mute, master-volume smoothing).

## Avalonia host — Setup tab

- The cross-platform `App.Avalonia` host gains its first ported tab. Wired to
  Core settings: app-setup toggles, encoder sensitivity, encoder feel
  (acceleration + smoothing), theme (light/dark/follow-system), volume-overlay
  preferences, controller pairing, factory reset, and log/settings shortcuts.
- Establishes the Avalonia settings lifecycle with a binding-init-order
  settings-wipe guard (the v2.61.2 fix, re-applied in Avalonia's lifecycle).
- Setup features that depend on not-yet-ported subsystems (audio-backend
  switching, global hotkey capture, device diagnostics, software updates,
  import/export, diagnostics zip, first-run wizard, About) land alongside those
  subsystems in later Phase 1 PRs.

## Firmware

No firmware changes. Requires firmware protocol **v2.24** or later (v2.25
current, backward-compatible).
