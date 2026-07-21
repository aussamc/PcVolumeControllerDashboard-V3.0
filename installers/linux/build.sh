#!/usr/bin/env bash
#
# Builds the Linux distributables for the Avalonia host from a self-contained
# linux-x64 publish folder: a Debian .deb, a universal AppImage, and a plain
# .tar.gz (the source the Arch PKGBUILD in installers/arch/ consumes).
#
# Usage: build.sh <publish-dir> <version> <output-dir>
#   <publish-dir>  self-contained linux-x64 publish (the exe + runtime + assets)
#   <version>      e.g. 3.14.2
#   <output-dir>   where the .deb / .AppImage / .tar.gz are written
#
# Run on Ubuntu (CI). Requires: dpkg-deb (dpkg), wget/curl, and FUSE-less
# appimagetool (fetched here, run with --appimage-extract-and-run). The .desktop,
# icon and udev rule live next to this script.
set -euo pipefail

PUBLISH="${1:?publish dir}"
VERSION="${2:?version}"
OUTDIR="${3:?output dir}"

APP="pcvolumecontroller"
EXE="PcVolumeControllerDashboard.Avalonia"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DESKTOP="$SCRIPT_DIR/$APP.desktop"
ICON="$SCRIPT_DIR/$APP.png"        # 32x32 (the app's only icon frame)
ICON_DIR="hicolor/32x32/apps"
UDEV="$SCRIPT_DIR/70-$APP.rules"   # uaccess tag for the ESP32's /dev/ttyACM* node
UDEV_DIR="usr/lib/udev/rules.d"

mkdir -p "$OUTDIR"

# ── .deb ────────────────────────────────────────────────────────────────────
# Layout: the app in /opt/<app>, a /usr/bin symlink on PATH, a .desktop launcher
# and the icon. Runtime is self-contained, so only the base native libs Avalonia
# needs on Linux are declared as Depends (a first-pass list; refine on real installs).
DEB_ROOT="$(mktemp -d)"
install -d "$DEB_ROOT/opt/$APP" \
           "$DEB_ROOT/usr/bin" \
           "$DEB_ROOT/usr/share/applications" \
           "$DEB_ROOT/usr/share/icons/$ICON_DIR" \
           "$DEB_ROOT/$UDEV_DIR" \
           "$DEB_ROOT/DEBIAN"
cp -a "$PUBLISH/." "$DEB_ROOT/opt/$APP/"
chmod +x "$DEB_ROOT/opt/$APP/$EXE"
ln -s "/opt/$APP/$EXE" "$DEB_ROOT/usr/bin/$APP"
cp "$DESKTOP" "$DEB_ROOT/usr/share/applications/$APP.desktop"
cp "$ICON" "$DEB_ROOT/usr/share/icons/$ICON_DIR/$APP.png"
cp "$UDEV" "$DEB_ROOT/$UDEV_DIR/70-$APP.rules"

INSTALLED_KB="$(du -sk "$DEB_ROOT/opt" | cut -f1)"
cat > "$DEB_ROOT/DEBIAN/control" <<CTRL
Package: $APP
Version: $VERSION
Section: sound
Priority: optional
Architecture: amd64
Maintainer: aussamc <noreply@github.com>
Installed-Size: $INSTALLED_KB
Depends: libc6, libx11-6, libice6, libsm6, libfontconfig1, libgl1
Description: PC Volume Controller Dashboard
 Cross-platform dashboard for the PC Volume Controller — a physical rotary-encoder
 + OLED per-channel volume controller (ESP32-S3) connected over USB serial.
CTRL

dpkg-deb --build --root-owner-group "$DEB_ROOT" "$OUTDIR/${APP}_${VERSION}_amd64.deb"

# ── AppImage ────────────────────────────────────────────────────────────────
APPDIR="$(mktemp -d)/AppDir"
install -d "$APPDIR/usr/bin" \
           "$APPDIR/usr/share/applications" \
           "$APPDIR/usr/share/icons/$ICON_DIR"
cp -a "$PUBLISH/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/$EXE"
cp "$DESKTOP" "$APPDIR/usr/share/applications/$APP.desktop"
cp "$DESKTOP" "$APPDIR/$APP.desktop"                 # appimagetool wants it at the root
cp "$ICON" "$APPDIR/usr/share/icons/$ICON_DIR/$APP.png"
cp "$ICON" "$APPDIR/$APP.png"                        # root icon matching Icon=

cat > "$APPDIR/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/PcVolumeControllerDashboard.Avalonia" "$@"
RUN
chmod +x "$APPDIR/AppRun"

TOOL="/tmp/appimagetool-x86_64.AppImage"
if [ ! -x "$TOOL" ]; then
  wget -qO "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod +x "$TOOL"
fi
# --appimage-extract-and-run so it works on CI runners without FUSE.
ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" \
  "$OUTDIR/PcVolumeControllerDashboard-${VERSION}-x86_64.AppImage"

# ── plain tarball ───────────────────────────────────────────────────────────
# Distro-agnostic payload: the publish folder plus the three shared assets, so a
# packager needs nothing from this repo but the tarball. installers/arch/build.sh
# consumes it, and it's attached to the release as the `source=` the PKGBUILD
# downloads — so the checksum a user verifies is the one CI built against.
TAR_ROOT="$(mktemp -d)/$APP-$VERSION"
install -d "$TAR_ROOT/app"
cp -a "$PUBLISH/." "$TAR_ROOT/app/"
chmod +x "$TAR_ROOT/app/$EXE"
cp "$DESKTOP" "$ICON" "$UDEV" "$TAR_ROOT/"

# Reproducible-ish: fixed owner/mtime and a sorted member order, so an unchanged
# publish yields an unchanged sha256 and the PKGBUILD's checksum stays meaningful.
tar --create --gzip \
    --owner=0 --group=0 --numeric-owner --sort=name \
    --mtime="@0" \
    --directory "$(dirname "$TAR_ROOT")" \
    --file "$OUTDIR/$APP-$VERSION-linux-x64.tar.gz" \
    "$APP-$VERSION"

echo "── built Linux packages ──"
ls -la "$OUTDIR"
sha256sum "$OUTDIR/$APP-$VERSION-linux-x64.tar.gz"
