# v3.22.3 — Instant knob response on slow audio devices (async write queue)

Fixes encoder turns, OLED updates and the volume overlay lagging by seconds when the
controlled app plays through a slow audio endpoint (network speakers such as Sonos,
Bluetooth). App-only; no firmware change or reflash.

## The bug

v3.22.2 removed a real session-enumeration cost, but the dominant one turned out to be
the **write itself**: a per-app session volume write on a network endpoint was measured
at **250–550 ms per call** (a Sonos Roam; the same endpoint's *master* volume writes
take under 1 ms, which is why the Master channel never felt slow). Writes ran inline on
the UI thread, so a 20-detent encoder turn became 20 serialized slow writes — the
encoder debounce never got a chance to coalesce them, and the OLED/overlay updates
queued behind the last one. Result: an 8-second volume ramp for a one-second knob turn.

## The fix

Audio **writes** now go through a new `AudioWriteQueue` — a dedicated background
writer with per-key coalescing:

- **The UI never waits for the device.** An encoder step computes its result from a
  cached read, updates the overlay and OLEDs immediately from that predicted value,
  and queues the write.
- **Slow devices get catch-up writes, not a backlog.** Relative encoder deltas sum
  while a write is in flight (one summed write per device round-trip); absolute sets
  (presets, volume smoothing) and mutes are latest-value-wins.
- **COM stays single-threaded.** The worker owns its own backend instance (WASAPI /
  PipeWire), created on the worker thread; VoiceMeeter (one Remote-DLL login per
  process, local fast calls) keeps using the shared backend. A backend switch rebuilds
  the write instance.
- WASAPI backend now also picks up default-device changes on every read/write (not
  just target enumeration), so the write-only instance follows device switches.

Rapid mute toggles are also pending-aware, so button mashing against a slow device
alternates correctly instead of getting stuck.

## Compatibility

- Required controller firmware protocol: **v2.24** (unchanged).
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8).
