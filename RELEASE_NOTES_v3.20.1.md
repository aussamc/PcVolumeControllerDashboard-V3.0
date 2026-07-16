# v3.20.1 — Download & install button on manual update check

Fixes a gap where clicking **Check for updates** and finding a new version gave you
no way to install it from that box — you had to restart the app (to get the auto-check
banner) or download it manually. App-only; no firmware change or reflash.

## What changed

- **"Download & install" button in the Software Updates box.** When a manual
  *Check for updates* finds a newer release, the Software Updates box now shows a
  **Download & install** button (with download progress and a one-click **Install now**
  once verified), the same flow as the auto-check banner. The banner and the box share a
  single install flow, so they always show the same state. When there's no installable
  package for your platform (e.g. macOS) or you launched with `--safe`, the button stays
  hidden and **View release** remains the fallback.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
