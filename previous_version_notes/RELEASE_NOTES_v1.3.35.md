# PC Volume Controller Dashboard v1.3.35

## Main focus

Encoder smoothing and log cleanup.

## Changes

- Dashboard version updated to v1.3.35.
- Required ESP32 protocol remains v1.3.33+.
- ESP32 firmware project remains `Computer_Volume_Controller_v1.3.33` because this patch does not make a functional firmware change.
- Added dashboard-side encoder direction stabilisation.
- Added isolated reverse-step rejection during rapid same-direction encoder movement.
- Added per-encoder event rate limiting.
- Added encoder event coalescing to reduce rapid serial/audio update bursts.
- Reduced encoder-related log spam unless Advanced Debug Logging is enabled.
- Added bundled `tools/esptool.exe` where available.

## Notes

The encoder smoothing is a dashboard-side safety layer. The ESP32 firmware debounce/filtering from the previous firmware version remains in place.
