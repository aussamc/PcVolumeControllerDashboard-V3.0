# Phase 0.5 Linux Derisk — Results

**Machine:** CachyOS (Arch-based), Wayland/KDE, PipeWire 1.6.7  
**Date:** 2026-06-24  
**Agent:** Claude Sonnet 4.6 running natively on the CachyOS box

---

## Q1 — Serial: does `Core.SerialService` open the ESP32 CDC port on Linux?

**Verdict: PASS**

Probe run on 2026-06-23 with ESP32 connected at `/dev/ttyACM0`:

```
Ports found: /dev/ttyACM0
Opened /dev/ttyACM0 @ 115200. Reading for 8s…
  RX  HELLO,PC_VOLUME_CONTROLLER,2.25,6,0x0000103A
  RX  ENC,1,1
  … (encoder + button events)
  RX  BTN_SHORT,2
PASS serial: opened /dev/ttyACM0 and received 91 line(s), including HELLO.
```

`System.IO.Ports.SerialPort` works natively on CachyOS/Arch with zero code changes.
DTR/RTS-off logic is portable. The `HELLO` handshake and live encoder/button events
were received correctly.

### Arch/CachyOS setup notes (one-time)

| Item | Detail |
|------|--------|
| Serial group | `uucp` (not `dialout` — that group does not exist on Arch) |
| Add user | `sudo usermod -aG uucp samuel` + re-login |
| .NET SDK | `sudo pacman -S dotnet-sdk` (10.0.109 used here) |

---

## Q2 — Audio: can we change a per-app volume from .NET via `wpctl`?

**Verdict: PASS — wpctl shell-out works perfectly**

`wpctl` is at `/usr/bin/wpctl`. The complete set/readback cycle was run directly:

```
$ wpctl get-volume 87          → Volume: 1.00
$ wpctl set-volume 87 0.30    → exit 0
$ wpctl get-volume 87          → Volume: 0.30   ✓
$ wpctl set-volume 87 1.00    → (restored)
```

Node 87 = the **Google Chrome** audio stream (visible in `wpctl status` under `Streams:`).

**What the .NET probe does** (`Probe.cs` line 106):  
`Process.Start("wpctl", "set-volume {node} {scalar}")` → reads stdout/stderr → checks exit code.  
This is a straightforward shell-out; no native binding is needed.

### Gotchas

1. **Use the parent stream ID, not the sub-channel port IDs.**  
   `wpctl status` shows Chrome as:
   ```
   87. Google Chrome
        79. output_FR  > ALC897 Analog:playback_FR  [active]
        89. output_FL  > ALC897 Analog:playback_FL  [active]
   ```
   `wpctl get-volume 87` works; `wpctl get-volume 79` → `Node '79' not found`.  
   The probe should pass the **stream group ID** (87), not the port sub-IDs.

2. **Stream IDs are ephemeral** — they change each time the app opens/closes audio.
   The production implementation will need to enumerate streams dynamically (via
   `wpctl status` output parsing or `pw-cli`) rather than storing IDs.

3. **PipeWire version**: 1.6.7 — `wpctl set-volume` accepts a `0.0–1.0` float scalar,
   which is exactly what the spike code sends (`volumePercent / 100.0`).

4. **No `pactl` fallback needed** — `wpctl` is present and functional. `pactl` is also
   available at `/usr/bin/pactl` as a backup.

---

## Environment summary

| Item | Status |
|------|--------|
| OS | CachyOS, Wayland/KDE |
| PipeWire | 1.6.7 — running |
| `wpctl` | `/usr/bin/wpctl` — functional |
| `pactl` | `/usr/bin/pactl` — available (fallback) |
| `dotnet-sdk` | 10.0.109 — installed |
| `uucp` group | Exists; `samuel` is a member |
| `dialout` group | Does not exist on Arch/CachyOS — `uucp` is correct |
| ESP32 device | `/dev/ttyACM0` — connected and verified |

---

## Conclusion

- **Q1 (serial):** **DERISKED.** `System.IO.Ports.SerialPort` opens `/dev/ttyACM0`,
  receives the `HELLO` handshake, and streams encoder/button events on Linux with no
  code changes. Only Arch-specific detail: group is `uucp`, not `dialout`.

- **Q2 (audio):** **DERISKED.** `wpctl` shell-out is sufficient — no native PipeWire
  binding needed. The approach the spike uses maps directly to what the production
  `AudioBackend` will do.

**Both spike questions answered. No fundamental blockers for the Linux port.**
