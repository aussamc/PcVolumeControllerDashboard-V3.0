# v3.24.0 — Fast encoder turns are no longer erratic on Linux

Fixes volume jumping backwards and stalling during fast knob turns, and stops the
audio-backend setting describing itself in Windows terms on Linux and macOS.

No firmware change — **do not reflash**. Firmware v2.31 remains current.

## Fast turns jumped backwards and stalled (Linux)

Turning a knob quickly made the volume lurch the wrong way and then ignore several
detents before catching up. Turning slowly was fine, which made it look like an
encoder or debounce problem. It was neither.

`AudioWriteQueue` gives callers an instant *predicted* volume so the OLEDs and
overlay don't wait on a slow device. It discarded that prediction the moment a
key's queued writes drained, on the assumption that the device now reads back the
value just written — then re-seeded the next change from a device read.

That assumption holds on Windows, where WASAPI reads query the session directly. It
does not hold on Linux: `PipeWireAudioBackend` serves reads from a `pw-dump`
snapshot refreshed every 150 ms. Measured on real hardware, encoder detents arrive
every **18 ms median, 6 ms minimum** — so the re-seed routinely picked up a value
around eight detents old, and the volume snapped back to where it had been 150 ms
earlier. Slow turns gave the snapshot time to catch up, hiding it completely.

From the diagnostic log, a single continuous downward turn:

```
74%  →  62%  →  92%  →  92%  →  92%  →  44%
                 ↑ jumped up 30% mid-turn, then ignored three detents
```

### The fix

`IAudioBackend` gains `ReadStalenessMs` — how long a read can keep reporting a
pre-write value after a write completes. It defaults to **0**, so any backend
querying the device directly (WASAPI, VoiceMeeter) behaves exactly as before.
`PipeWireAudioBackend` reports **300 ms** (two refresh intervals, covering a refresh
already in flight plus the one that observes the write). `AudioWriteQueue` keeps its
prediction alive for that window past drain, so consecutive detents build on each
other instead of re-seeding from a stale read.

Measured on the same hardware and the same kind of fast back-and-forth turning:

| | applies | volume moved opposite to the turn | detent ignored (not at 0/100) |
|---|---|---|---|
| before | 521 | 73 (14.0%) | 103 (19.8%) |
| after | 183 | 2 (1.1%) | 0 (0.0%) |

The two residual readings are single applies at direction-reversal boundaries, where
a detent can't be attributed to one side or the other — not the multi-detent
snapbacks being fixed here.

## Audio backend no longer described in Windows terms

The first-run wizard's audio-backend step and the Setup tab both offered *"Windows
audio (WASAPI)"* and *"VoiceMeeter"* on every platform — so on Linux the running
backend (PipeWire) was labelled as Windows audio, and VoiceMeeter appeared selectable
despite being a Windows-only application.

- The system-audio option now names the backend the running OS actually uses:
  **Windows audio (WASAPI)**, **Linux audio (PipeWire)**, or **System audio**.
- **VoiceMeeter is disabled off-Windows**, with a note explaining why. Greyed out
  rather than hidden, so the page doesn't silently change shape between platforms.
- A settings file naming VoiceMeeter — written on Windows, or carried across by
  import — no longer shows VoiceMeeter as the active backend on a machine that can't
  run it. Backend *selection* was always correct; only the UI was misleading.

Both screens take their wording from one place, so they can't drift apart.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Matching firmware: **v2.31** (unchanged — no reflash).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
