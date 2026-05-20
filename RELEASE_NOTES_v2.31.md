# Release Notes — v2.31

## Audio reliability improvements

### Dictionary lookup cache for `FindTargetByKey()`
`FindTargetByKey()` previously performed a linear scan of `_audioTargets` on every call.
With 20+ sessions and 6 channels polling at 500 ms, this produced ~240+ linear scans per second.
A `Dictionary<string, AudioTargetItem>` cache (`_audioTargetCache`) is now rebuilt at the end of
every `RefreshAudioSessions()` call, making all subsequent `FindTargetByKey()` lookups O(1).
PROC: multi-session lookups in `FindSessionsForKey()` retain their existing COM-enumeration path
(same process name may map to multiple streams); only exact-key lookups benefit from the cache.

### Overlapping `RefreshAudioSessions()` guard
If `RefreshAudioSessions()` was called concurrently (e.g. from the device-notification callback and
the auto-refresh timer at the same time) COM session enumeration could throw.
An `Interlocked`-based flag (`_audioSessionRefreshInProgress`) now causes a second overlapping call
to log a skip message and return immediately, preventing concurrent COM enumeration.

### COM `SimpleAudioVolume` pattern audit
All `SimpleAudioVolume` accesses were audited. No instances are stored as long-lived class fields;
every access follows the acquire-use pattern (local variable, used within the same scope).
NAudio's `SimpleAudioVolume` does not implement `IDisposable`, so no `using` wrappers are needed.
The existing pattern is correct and no changes were required.

### Improved offline channel status messages
`RefreshAllChannelStates()` now produces more informative status strings:

| Situation | Previous status | New status |
|-----------|----------------|------------|
| MIC_INPUT assigned, no capture device found | "No device" | "No microphone" |
| Non-PROC key (e.g. DEVICE:) not found in target list | "App offline" / "Waiting" | "Target unavailable" |
| `IsAppOffline` for mic with no device | `false` | `true` |

PROC: key behaviour is unchanged ("App offline" / "Waiting" depending on `RebindFallback`).

## Version
- Dashboard: 2.31
- Required firmware protocol: 2.24 (unchanged)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
