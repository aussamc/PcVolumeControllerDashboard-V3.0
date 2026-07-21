# v3.23.5 — Linux serial access works on CH343-bridged boards

Packaging fix for Linux: the udev rule shipped with the `.deb`, AppImage and Arch
packages didn't match the v1.4 PCB, so the controller's serial port stayed
inaccessible after a clean install. Dashboard-only change; no firmware reflash needed.

## What was wrong

The rule granted access by tagging the device `uaccess`, which makes
systemd-logind hand an ACL to the active local session — no group membership, no
logout/login cycle. But it matched on Espressif's USB vendor ID (`0x303a`) alone,
on the assumption that the ESP32-S3 always enumerates via native USB CDC.

The v1.4 PCB doesn't. It routes serial through a WCH CH343 bridge, which
enumerates under QinHeng's vendor ID (`0x1a86:0x55d3`) with the ESP32 invisible
behind it. The rule never matched, no ACL was ever granted, and `/dev/ttyACM0`
stayed at its default `root:uucp` ownership.

The result was silent: users already in `uucp` (or `dialout` on Debian/Fedora)
saw the dashboard connect normally and never knew the rule was inert. Everyone
else got a port that wouldn't open, with the post-install notes pointing them at
the group fallback as though their hardware were unusual.

## The fix

- The udev rule now matches the CH343 bridge as well as the Espressif VID. The
  new match is pinned to the product ID (`0x1a86:0x55d3`), not the vendor ID
  alone — `0x1a86` covers a large share of generic USB-serial adapters, and
  granting an ACL to every one of them plugged into the machine is broader than
  this needs.
- The Arch post-install notes now name both supported wirings, so the group
  fallback reads as the exception it is.

Boards behind a CP210x or FTDI bridge still don't match and still need the group
route; that's called out in both the rule's comments and the install notes.

## Upgrading

Installing the new package reloads the rule, but udev applies tags at plug time —
**replug the controller** (or reboot) for the ACL to appear. If you added yourself
to `uucp`/`dialout` as a workaround, that membership is now redundant and can be
removed.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
