# PC Volume Controller Dashboard v1.3.38 Beta 3

## Version

```text
Dashboard version:       v1.3.38 Beta 3
Required ESP32 protocol: v1.3.37.1
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.37.1
```

## Changes since v1.3.38 Beta 1 buildfix2

```text
- Renamed the OLED Preview tab to OLED Setup.
- Moved OLED Display Settings from the Setup tab into OLED Setup.
- Updated OLED previews to use a 128x64 logical canvas matching the physical OLED resolution.
- Scaled the 128x64 preview canvas up to 256x128 on screen for readability.
- Arranged the six OLED previews in numeric order from OLED 1 to OLED 6.
- Moved the First Run wizard tab to the end of the tab list so it is out of the way during normal use.
- Updated dashboard version string to v1.3.38 Beta 3.
```

## Firmware

No ESP32 firmware behaviour changed in this beta, so the firmware source remains:

```text
Computer_Volume_Controller_v1.3.37.1
```


## Beta 3 changes

- Reworked the **OLED Setup** tab to show a full controller preview rather than standalone OLED cards.
- Kept the **OLED Display Settings** section at the top of the page for faster iteration.
- Arranged the full preview to show all 6 OLEDs in order across the top and the 6 encoder positions beneath them.
- Preserved the logical 128x64 OLED preview canvas while scaling it up on screen for readability.
