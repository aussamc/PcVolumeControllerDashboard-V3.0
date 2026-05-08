# PC Volume Controller Dashboard v1.3.38 Beta 6

## Version status

Dashboard version:       v1.3.38 Beta 6
Required ESP32 protocol: v1.3.38 Beta 6
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_6

## Changes made

### OLED dimming fix

The Beta 4 firmware used a direct linear mapping from brightness percentage to SSD1306 contrast:

```text
0-100% -> 0-255 contrast
```

That can still look fairly bright at low percentages on many OLED modules. Beta 5 changes this to:

```text
- Brightness 0%: explicitly powers the OLED off.
- Brightness 1-100%: uses a quadratic contrast curve for much lower low-end brightness.
- Low brightness also lowers SSD1306 pre-charge and VCOMH settings.
```

This should make low brightness levels visibly dimmer while keeping 100% full brightness available.

### Dashboard slider changes

- OLED Setup sliders are now clickable.
- Setup page encoder sensitivity slider remains clickable.
- Sliders are now shorter and left-aligned instead of stretching across the whole page.

### Controller preview feedback

- Encoder controls in the OLED Setup controller preview now briefly highlight when encoder input is received.
- This gives quick visual confirmation that the dashboard is seeing physical encoder events.

## Firmware note

Because this beta changes ESP32 OLED brightness behavior and protocol version, flash the included firmware source before testing the physical OLED dimming changes.
