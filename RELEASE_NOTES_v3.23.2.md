# v3.23.2 — Run-at-login entry stops pointing at folders that disappear

Bug fix: the dashboard could **silently stop launching at login**, most often right
after an automatic update. Dashboard-only change; no firmware reflash needed.

## What was wrong

The dashboard re-syncs its Windows "run at login" entry on every launch, so the entry
follows the app when it's moved or reinstalled. It recorded whatever path the process
happened to be running from — which is only correct when the app is running from where
it actually lives. Two launches don't satisfy that:

- **After an automatic update**, the new build is staged under the temp directory and
  run from there. The app recorded that temp path.
- **Developer builds** run out of `bin\Debug`. The app recorded that path.

Both folders are gone soon after — swept by Storage Sense, or emptied by the next
rebuild — leaving a startup entry pointing at an executable that no longer exists.
Windows then launches nothing at the next logon, with no error and no log.

The failure was unusually hard to pin down because it **repairs itself the moment you
look**: launching the dashboard by hand rewrites the entry back to the real installed
path, so anyone who checked the registry afterwards found it perfectly correct. The
symptom was an intermittent "it just didn't start this time" that never reproduced on
demand.

## The fix

- The per-launch re-sync now refuses to record a **transient location** (the temp
  directory, or a `bin\Debug` / `bin\Release` build output folder) and leaves the
  existing, working entry untouched — with a log line explaining why.
- Toggling **"Start program at login"** yourself still writes whatever the app is
  running from (an explicit action shouldn't silently do nothing), but now warns in
  the log when that location looks temporary.
- The guard is deliberately narrow: a **portable build** run from Downloads, a USB
  stick, or any other unusual-but-stable folder is a legitimate choice and is still
  recorded normally.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
