PC Volume Controller Dashboard v1.3.35

This version focuses on encoder smoothing and log cleanup.

Highlights:
- Dashboard-side encoder direction stabilisation.
- Per-encoder event rate limiting.
- Encoder event coalescing.
- Less encoder log spam unless Advanced Debug Logging is enabled.
- Required ESP32 protocol remains v1.3.33+.
- ESP32 firmware project remains Computer_Volume_Controller_v1.3.33 because no functional firmware change was made.
- tools/esptool.exe is bundled when available.

Build with:
  dotnet run
