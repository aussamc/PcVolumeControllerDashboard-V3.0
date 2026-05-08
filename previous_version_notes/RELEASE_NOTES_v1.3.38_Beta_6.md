# PC Volume Controller Dashboard v1.3.38 Beta 6

## Version status

Dashboard version:       v1.3.38 Beta 6
Required ESP32 protocol: v1.3.38 Beta 6
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_6

## Changes

### 1. Click-and-drag sliders

The dashboard sliders now support click, hold, and drag behaviour instead of only jumping once when clicked.

Updated sliders:

- OLED brightness
- Disconnected OLED sleep timeout
- Connected OLED idle timeout
- Encoder sensitivity

### 2. Connected OLED idle mode

The OLED Setup page now includes a connected idle feature for the controller display while the dashboard is connected.

New settings:

- Connected idle action:
  - Turn off display
  - Dim brightness to 30%
- Connected idle timeout slider
  - Default: 10 minutes
  - Range: 1 to 60 minutes

The controller enters this state when there is no controller input and no display/volume state change from the PC for the selected timeout.

### 3. Anti-burn-in

Added an anti-burn-in option on the OLED Setup page.

- Default: enabled
- Firmware periodically shifts the OLED display offset while active.
- This helps reduce static-pixel wear on OLED modules.

### 4. Firmware protocol update

The OLED config command has been extended from:

```text
OLEDCFG,<mode>,<brightness>,<disconnectedTimeout>
```

to:

```text
OLEDCFG,<mode>,<brightness>,<disconnectedTimeout>,<connectedIdleAction>,<connectedIdleTimeout>,<antiBurnIn>
```

The ESP32 acknowledgement has also been extended to include these values.

## Notes

This beta changes ESP32 behaviour, so the included Beta 6 firmware should be flashed before testing the new OLED idle features.
