# PC Volume Controller Dashboard v2.0

## Version status

Dashboard version:       v2.0
Required ESP32 protocol: v2.0
ESP32 firmware folder:   Computer_Volume_Controller_v2.0
Target hardware:         v1.4 PCB — ESP32-S3-DevKitC-1-N16R8

## Changes

1. **Major hardware port: prototype → v1.4 PCB (6-channel)**
   - Ported from a single-encoder/single-OLED prototype to the v1.4 production PCB.
   - ESP32-S3-DevKitC-1-N16R8 replaces the prototype ESP32 board.
   - 6 independent rotary encoders (with push buttons) replacing the single prototype encoder.
   - 6 SSD1315 OLED displays driven through a TCA9548A 8-channel I2C multiplexer.
   - I2C on GPIO 9 (SDA) / 10 (SCL); mux address 0x70.

2. **New firmware: Computer_Volume_Controller_v2.0**
   - Protocol version bumped to `2.0`; `CHANNEL_COUNT = 6`.
   - Single shared `Adafruit_SSD1306` framebuffer; TCA9548A mux channel selected before each I2C operation.
   - Encoder pin assignments: ENC_A `{1,4,7,12,15,18}`, ENC_B `{2,5,8,13,16,21}`, ENC_SW `{3,6,11,14,17,38}`.
   - All encoder pins use `INPUT_PULLUP` — safe to flash and test OLEDs before encoders are installed.
   - Per-channel splash "Ch N of 6 / Waiting for PC" on startup.
   - Per-channel anti-burn-in pixel offset, brightness control, and display power on/off.

3. **Dashboard: protocol version updated**
   - `RequiredProtocolVersion` updated from `"1.3.38 Beta 7"` to `"2.0"`.
   - Dashboard version constant updated to `"2.0"`.
   - Assembly version updated to `2.0.0.0`.

4. **Auto-connect default changed to enabled**
   - `AutoConnectOnLaunch` now defaults to `true` for new installs.
   - Existing users are migrated to `true` on first launch of v2.0 (settings migration v0 → v1).

5. **OLED brightness default raised to 100%**
   - Default `OledBrightnessPercent` changed from 80% to 100% (hardware maximum for SSD1315).

## Firmware impact

A full reflash to `Computer_Volume_Controller_v2.0` is required.
This firmware is **not** compatible with the v1.x prototype hardware.
