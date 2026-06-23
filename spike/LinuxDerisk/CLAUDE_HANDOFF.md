# Handoff brief — paste this into Claude Code on the CachyOS machine

Copy everything in the fenced block below into a fresh `claude` session running
in the repo root on CachyOS. It's self-contained (the Linux Claude has no memory
of this project).

---

```
You are helping run a throwaway de-risk SPIKE for the PC Volume Controller
Dashboard v3 (a cross-platform Avalonia rewrite). I'm on CachyOS (Arch-based,
PipeWire by default). You're running natively on this Linux box.

GOAL: answer two yes/no questions before we invest in the Linux platform work,
then report the results. This is throwaway probe code — do not polish it.

  Q1 (serial): does the extracted Core SerialService open the ESP32 USB-CDC port
      and read lines on Linux?
  Q2 (audio):  can we change a per-app (sink-input) volume from .NET via wpctl?

The spike lives at spike/LinuxDerisk and has a headless mode that prints PASS/
FAIL lines — use it (no GUI needed).

STEPS:
1. Confirm prerequisites: `dotnet --version` (need .NET 10 SDK), `which wpctl`,
   and `ls -l /dev/ttyACM*`. On Arch/CachyOS the serial device group is `uucp`
   (NOT dialout). If I'm not in it, tell me to run `sudo usermod -aG uucp $USER`
   and re-login — you can't fix group membership for me.
2. Build: `dotnet build spike/LinuxDerisk/LinuxDerisk.csproj`.
3. Serial probe (plug in the ESP32 first):
   `dotnet run --project spike/LinuxDerisk -- --headless --no-audio --seconds 8`
   Expect a `PASS serial:` line and ideally an `RX HELLO,PC_VOLUME_CONTROLLER,...`.
4. Audio probe — first list nodes:
   `dotnet run --project spike/LinuxDerisk -- --headless --no-serial`
   Ask me to start an app playing audio (browser/Spotify), find its stream id in
   the `Streams:` section of the wpctl output, then set+verify its volume:
   `dotnet run --project spike/LinuxDerisk -- --headless --no-serial --node <ID> --volume 30`
   A `PASS audio:` line with a get-volume readback means it worked. You can also
   run `wpctl get-volume <ID>` yourself to double-check.
5. Report a short verdict: PASS/FAIL for each question, the exact wpctl commands
   that worked, the serial port name, and any gotchas hit. If wpctl shell-out was
   insufficient and a native binding looks necessary, say so explicitly.

Write the verdict to spike/LinuxDerisk/RESULTS.md so I can carry it back. Keep it
to a few lines per question. Don't change anything outside spike/.
```

---

## After the spike

Bring the verdict (or `spike/LinuxDerisk/RESULTS.md`) back to the Windows session
so it can be recorded in project memory and used to scope Phase 1 (Avalonia port)
and Phase 2 (Linux audio backend). Then the spike branch can be deleted.
