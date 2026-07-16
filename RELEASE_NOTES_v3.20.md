# v3.20 — Setup-tab polish, single-target Master, and an OLED anti-burn fix

A batch of Setup/OLED UX improvements, a channel-mapping fix, and a firmware
display fix.

> **Firmware reflash required for the OLED fix.** The Large-Volume anti-burn fix
> is on the controller, so flash **firmware v2.28** (`Computer_Volume_Controller_v2.28/`)
> to get it. The wire protocol is unchanged (still v2.24), so an un-reflashed
> controller keeps working — it just won't have the display fix. Everything else in
> this release is app-only and needs no reflash.

## What changed

- **Sliders sit next to their heading.** The OLED Brightness, Disconnected-sleep-timeout
  and Connected-idle-timeout sliders, and the Encoder Sensitivity slider, now use a
  `heading · slider · value` row (the reported value sits to the right of the slider),
  matching the Volume Overlay card — instead of stacking the value above the slider.
- **"Run setup wizard again" moved to Maintenance.** The re-run-wizard control now lives
  in the Setup tab's **Maintenance** section, next to Export/Import/Factory-reset, rather
  than in Application Setup.
- **Master starts as a single target, not a pool.** A freshly-assigned channel (e.g.
  Master) was being shown as a multi-app "(pool)" because its single target was mirrored
  into the pool list. A pool now means **two or more** targets; a settings migration
  clears the stray single-entry list so existing installs are corrected on next launch.
  Genuine multi-app pools are untouched.
- **Large-Volume OLED no longer wraps with anti-burn on (firmware v2.28).** The size-2
  channel-name header sat flush at the top row, so the anti-burn pixel shift could wrap
  its top row by ~1px. The header now sits 3px down, keeping all Large-Volume content
  within a safe band so the shift can't wrap either edge. The dashboard's OLED preview
  mirrors the same layout.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Firmware **v2.28** recommended (adds the Large-Volume anti-burn fix; reflash to get it).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
