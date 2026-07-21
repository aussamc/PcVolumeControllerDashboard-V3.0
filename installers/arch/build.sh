#!/usr/bin/env bash
#
# Renders PKGBUILD.in and builds the Arch package from the Linux tarball that
# installers/linux/build.sh produces.
#
# Usage: build.sh <tarball> <version> <output-dir>
#   <tarball>     pcvolumecontroller-<version>-linux-x64.tar.gz
#   <version>     e.g. 3.23.4
#   <output-dir>  where the .pkg.tar.zst and the rendered PKGBUILD are written
#
# Run on Arch (CI uses the archlinux:base-devel container) as a NON-root user —
# makepkg refuses to run as root.
#
# The rendered PKGBUILD points `source=` at the release URL, which won't exist
# yet at build time. That's deliberate and not worked around: makepkg uses a
# matching file already sitting in the build dir instead of downloading, and
# still verifies it against sha256sums. So CI validates the exact PKGBUILD a
# user will run post-release, rather than a local-source variant of it.
set -euo pipefail

TARBALL="${1:?tarball}"
VERSION="${2:?version}"
OUTDIR="${3:?output dir}"

APP="pcvolumecontroller"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[ "$(id -u)" -ne 0 ] || { echo "build.sh: makepkg cannot run as root" >&2; exit 1; }

mkdir -p "$OUTDIR"
OUTDIR="$(cd "$OUTDIR" && pwd)"

WORK="$(mktemp -d)"
cp "$SCRIPT_DIR/PKGBUILD.in" "$SCRIPT_DIR/$APP.install" "$WORK/"
cp "$TARBALL" "$WORK/$APP-$VERSION-linux-x64.tar.gz"

SHA="$(sha256sum "$WORK/$APP-$VERSION-linux-x64.tar.gz" | cut -d' ' -f1)"
sed -e "s/@VERSION@/$VERSION/g" -e "s/@SHA256@/$SHA/g" \
    "$WORK/PKGBUILD.in" > "$WORK/PKGBUILD"
rm "$WORK/PKGBUILD.in"

cd "$WORK"
makepkg --force --nodeps --noconfirm

# namcap is advisory — a self-contained .NET tree trips several of its checks
# (bundled libs, no debug symbols) that are correct for this package.
if command -v namcap >/dev/null 2>&1; then
    echo "── namcap (advisory) ──"
    namcap ./*.pkg.tar.zst || true
fi

cp ./*.pkg.tar.zst "$OUTDIR/"
cp PKGBUILD "$OUTDIR/"

echo "── built Arch package ──"
ls -la "$OUTDIR"
