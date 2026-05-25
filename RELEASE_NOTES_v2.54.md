# Release Notes — v2.54

## Feature: Hardware identity pairing

The dashboard can now read an optional **chip ID** from the ESP32 controller and
use it to recognise which physical device it is talking to.

### Protocol change (backward-compatible)

The `HELLO` message gains an optional 5th field:

```
HELLO,PC_VOLUME_CONTROLLER,<protocol>,<channels>,<chipId>
```

Old firmware (< v2.25) sends only 4 fields; the dashboard treats the chip ID as
empty in that case and behaves exactly as before.

### Pairing logic

| Stored chip ID | Received chip ID | Result                                |
|---------------|-----------------|---------------------------------------|
| empty          | any non-empty   | **First pair** — ID stored, logged    |
| matches        | same ID         | Normal connection                     |
| differs        | different ID    | **Mismatch warning** logged to console and log file |
| any            | empty           | No identity check (old firmware)      |

The paired chip ID is stored in `settings.json` as `LastDeviceChipId`.
A "Forget controller" button to clear this field will be added in v2.55.

### Other changes

- `_connectedDeviceChipId` runtime field tracks the chip ID of the current session.
- Diagnostics dump includes both the connected chip ID and the paired chip ID.
- `EspStatusTextBlock` shows `chip <id>` when a chip ID is present.
- Chip ID field is reset to empty on disconnect (both connection-phase and
  `DisconnectSerial`).

---

## Compatibility

- Dashboard: v2.54
- Required firmware protocol: v2.24 (no reflash required for existing functionality)
- Hardware: v1.4 PCB (6-channel, ESP32-S3-DevKitC-1-N16R8)
