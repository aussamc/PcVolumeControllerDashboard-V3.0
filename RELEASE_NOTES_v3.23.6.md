# v3.23.6 — Linux udev rule now actually grants serial access

Second half of the Linux serial-access fix started in v3.23.5. The rule matched the
right hardware after that release but still granted nothing; it needed renumbering
from `99-` to `70-`. Dashboard-only change; no firmware reflash needed.

## What was wrong

Granting access via `uaccess` takes two parties. Our rule sets the tag; systemd's
`73-seat-late.rules` is what turns it into an ACL:

```
TAG=="uaccess", ENV{MAJOR}!="", RUN{builtin}+="uaccess"
```

udev reads rule files in lexical order, and ours shipped as
`99-pcvolumecontroller.rules` — so the tag was set at priority 99, long after line
73 had already tested for it and moved on. The tag landed in the udev database and
the builtin that creates the ACL was never invoked.

This failed quietly in the most misleading way available: `udevadm info` reports
`CURRENT_TAGS=:uaccess:systemd:`, which reads as success, while `getfacl` on the
device shows no user entry at all. The device keeps its default `root:uucp`
ownership and only members of that group can open it.

The rule carried the `99-` prefix from the day it was introduced, so **`uaccess`
has never worked in any release** — on any board, including the native-USB-CDC
Espressif wiring the rule was originally written for. The VID mismatch fixed in
v3.23.5 was a real second defect sitting on top of this one; correcting it made the
tag appear and exposed that the tag alone did nothing.

Anyone for whom serial "just worked" was being carried by `uucp`/`dialout`
membership the whole time — which is exactly what the rule existed to make
unnecessary.

## The fix

- Renamed the rule to `70-pcvolumecontroller.rules` so it is read before
  `73-seat-late.rules`. Packaging paths in `installers/linux/build.sh` and
  `installers/arch/PKGBUILD.in` updated to match.
- Documented the constraint in the rule's own header, including the exact
  symptom (tag present, no ACL), so the prefix isn't treated as arbitrary and
  renumbered back.

## Verified

On a v1.4 PCB (CH343 bridge, `1a86:55d3`) on CachyOS/KDE Wayland, after installing
the rule at priority 70 and replugging:

```
$ getfacl /dev/ttyACM0
user::rw-
user:samuel:rw-     ← granted by uaccess
group::rw-
mask::rw-

$ ls -l /dev/ttyACM0
crw-rw----+ 1 root uucp 166, 0 ...   ← '+' marks the ACL
```

The same check against the v3.23.5 rule at priority 99 produces no `user:` entry
and no `+`.

## Upgrading

udev applies tags at plug time, so **replug the controller** (or reboot) after
installing. If you added yourself to `uucp`/`dialout` as a workaround, that
membership is now genuinely redundant — `getfacl` showing a `user:` line for your
account confirms the ACL, not the group, is doing the work.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
