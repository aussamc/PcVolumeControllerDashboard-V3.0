# v3.16 — First-run defaults & OLED polish

A batch of better out-of-box defaults plus OLED display improvements. **Requires a
controller reflash to v2.26** for the OLED changes to appear on the device.

## First-run defaults (fresh installs only)

These change only the defaults for a new install — your existing settings are untouched.

- **Channels start cleaner** — only channel 1 is pre-assigned (Master). Channels 2–6
  start unassigned; the setup/assign UI suggests names (Browser, Music, **Voice Chat**,
  Game, Microphone) without binding them. No channel starts as a multi-app pool.
- **Large Volume Number** is the default OLED display mode.
- **Runs in the tray by default** — Minimize to tray, Start minimized to tray, and Start
  at login now default on (it's a background utility).
- **Encoder acceleration on by default** (Medium) for a better feel out of the box;
  volume smoothing stays off.

## OLED display

- **Redesigned "Large Volume Number"** — bigger channel name and a much bigger number;
  the separate "Muted/Unmuted" line is gone. When a channel is muted the number is
  replaced by the word **"MUTE"**.
- **Anti-burn-in no longer clips the bottom text** — the pixel-shift used to push the
  bottom line off the screen; every display mode now reserves a safe margin so it never
  does.
- **Preview mirrors the shift** — the on-screen OLED preview now reflects the anti-burn
  pixel-shift (and the toggle updates it immediately).

## Layout

- The encoder-sensitivity slider is left-aligned, and the OLED sliders sit tidily under
  their headings.

## Firmware

- **v2.26** — redesigned Large Volume layout + the anti-burn margin fix. A reflash is
  required to see the OLED changes. This is a display-only change; the wire protocol is
  unchanged, so the dashboard still connects to any firmware ≥ v2.24.

## Compatibility

- Required controller firmware protocol: **v2.24** (firmware v2.26 is
  backward-compatible).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
