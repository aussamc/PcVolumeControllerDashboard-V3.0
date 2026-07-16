# v3.22 — Setup connection & OLED polish

Follow-up refinements to the v3.21 Audio/Setup declutter. Layout and status-readout
only — no change to audio, channel, or controller behaviour. App-only; no firmware
change or reflash.

## What changed

- **Controller status is split by tab.** The Setup tab's **Controller Connection** card
  now shows the full detail — **state, protocol version, and chip ID** (plus a warning
  line for incompatible firmware or a channel-count mismatch). The Audio tab keeps a
  **simple Connected/Disconnected** at a glance, now with a colour-coded status dot
  (green connected, amber connecting, red for a problem, grey idle).

- **Controller Connection is pinned to the top of Setup.** It's now a always-visible
  card at the top of the Setup tab rather than a collapsible section, so the connection
  state and controls are the first thing you see.

- **OLED Display Settings rows aligned.** Every row now shares a common label column and
  a uniform gap, so the labels and controls line up instead of stepping in and out.

- **Encoder Sensitivity and Encoder Feel merged into "Encoder Setup".** The two separate
  sections are now one collapsible section with **Sensitivity** and **Feel** sub-headings.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
