# PC Volume Controller Dashboard v1.3.38 Beta 4

## Version status

```text
Dashboard version:       v1.3.38 Beta 4
Required ESP32 protocol: v1.3.38 Beta 4
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_4
```

## Changes

- Added **Save OLED Setup** button at the bottom of the **OLED Setup** page.
- OLED setup settings now save locally and send an `OLEDCFG` command to the connected ESP32.
- Dashboard sends OLED configuration automatically after a valid ESP32 identity handshake.
- ESP32 firmware now handles:
  - Display mode
  - OLED brightness/contrast
  - No-dashboard dim/sleep timeout
- ESP32 firmware now replies with `OLEDCFG_ACK` after applying display settings.
- Dashboard logs the `OLEDCFG_ACK` response.

## Firmware note

This beta includes a real ESP32 firmware behavior change, so the firmware project was renamed to:

```text
Computer_Volume_Controller_v1.3.38_Beta_4
```

Flash this firmware before expecting physical OLED display settings to update from the dashboard.
