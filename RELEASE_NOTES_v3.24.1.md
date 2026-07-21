# v3.24.1 — In-app updates install natively on Arch

The built-in updater now installs the pacman package on Arch and its derivatives,
instead of downloading a Debian package and handing it to the desktop.

No firmware change — firmware v2.31 remains current.

## What was wrong

`UpdateInstaller.DetectPlatform()` recognised exactly two Linux cases: running from
an AppImage, or "everything else". Everything else resolved to `UpdatePlatform.LinuxDeb`.

So on Arch, CachyOS, EndeavourOS or Manjaro the updater downloaded
`pcvolumecontroller_<ver>_amd64.deb` and ran `xdg-open` on it — handing a Debian
package to a system that cannot install one. The release has shipped a perfectly
good `pcvolumecontroller-bin-<ver>-1-x86_64.pkg.tar.zst` since v3.22, but nothing
ever selected it.

## The fix

- New `UpdatePlatform.LinuxArch`, selecting the `.pkg.tar.zst` asset. The match is on
  the full compound extension so the plain `linux-x64.tar.gz` in the same release can
  never be picked up by mistake.
- Detection reads `/etc/os-release`, treating `ID=arch` or an `ID_LIKE` containing
  `arch` as the Arch family — `ID_LIKE` is what covers the derivatives, since each
  sets its own `ID`.
- Applying runs `pkexec pacman -U --noconfirm <package>`. `pkexec` raises the
  desktop's standard polkit password dialog, so the install genuinely happens rather
  than being handed off. The dashboard stays running (pacman replaces files under a
  live process safely) and the new build is picked up on next launch.
- The progress line now says what will actually happen: *"Installing… approve the
  password prompt, then restart the dashboard."*
- The resolved install medium and matched asset are logged, so a future
  wrong-package report can be diagnosed from the log rather than reasoned about from
  scratch.

Running from an AppImage still takes priority over distro detection on any distro —
that path replaces its own file and never involves the package manager.

## Still unresolved

Non-Debian, non-Arch distributions (Fedora, openSUSE, etc.) continue to fall through
to the `.deb`, which is wrong there for exactly the same reason it was wrong on Arch.
Fixing that needs an RPM artifact in the release first; detection alone can't help.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Matching firmware: **v2.31** (unchanged — no reflash).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
