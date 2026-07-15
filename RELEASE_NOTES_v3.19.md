# v3.19 — Automatic updates: check, download & install

The dashboard now checks for a newer release on its own, and can download and install it
for you with one click, instead of only checking when you press **Check for updates**. No
settings-file migration, protocol, or firmware changes — the controller does **not** need
a reflash.

## Automatic update checking

- **Background check on launch** (and periodically while the app runs) queries GitHub for
  a newer release. It's throttled so rapid restarts don't re-query, and it's controlled by
  the existing **"Automatically check for updates"** preference (on by default) added in
  v3.18 — turn it off to disable the automatic checks entirely.

- **Update banner** — when a newer version is found, a banner appears at the top of the
  **Audio** tab with three choices:
  - **View release** — opens the release page in your browser.
  - **Skip this version** — stops reminding you about this release until a newer one ships.
  - **Remind me later** — hides the banner until the next check.

- **Desktop notification** — if tray notifications are enabled, you also get a toast when
  an update is available, so you see it even when the window is minimised to the tray.

The manual **Check for updates** button in the Debug/diagnostics section still works exactly
as before.

## Download & install

- **Download & install** — the update banner's button downloads the right file for your
  platform (the Windows installer, or on Linux the AppImage / `.deb`), verifies its size
  and **SHA-256 checksum**, then launches the installer. Windows shows the installer so you
  can confirm each step; on Linux a new AppImage relaunches in place and a `.deb` is handed
  to your system package installer.

- **Automatically download & install updates** (the v3.18 preference, still **off** by
  default) is now live: when on, an available update **downloads in the background** so the
  banner offers a one-click **Install now**. Launching the installer is always an explicit
  click — the app never runs an installer or self-replaces behind your back.

- **Skip / Remind me later** — skip a version to stop being reminded until a newer one
  ships, or dismiss the banner until the next check.

- **Safe mode & platform** — auto-check, auto-download, and the install button are all
  suppressed under `--safe`. macOS has no signed build yet, so there the banner offers only
  **View release**.

## Compatibility

- Required controller firmware protocol: **v2.24**.
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
