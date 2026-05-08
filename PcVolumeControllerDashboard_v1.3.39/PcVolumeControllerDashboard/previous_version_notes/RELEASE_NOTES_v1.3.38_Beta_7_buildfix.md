# PC Volume Controller Dashboard v1.3.38 Beta 7 buildfix

This buildfix resolves a dashboard compile error in Beta 7.

## Version status

Dashboard version:       v1.3.38 Beta 7 buildfix
Required ESP32 protocol: v1.3.38 Beta 7
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_7

## Fixed

- Added the missing `GetOledConnectedIdleActionDisplayName` helper used by the OLED Setup preview text.
- The helper now maps each connected-idle action to a readable label:
  - `Off` -> `Turn off display`
  - `DimTo10` -> `Dim brightness to 10%`
  - `DimTo20` -> `Dim brightness to 20%`
  - `DimTo30` -> `Dim brightness to 30%`
  - `DimTo40` -> `Dim brightness to 40%`
  - `DimTo50` -> `Dim brightness to 50%`
  - `DimTo60` -> `Dim brightness to 60%`
  - `DimTo70` -> `Dim brightness to 70%`

## Notes

- This is a dashboard-only buildfix.
- Existing Beta 7 firmware remains correct.
- Previous version notes are stored in `previous_version_notes/` so only current build notes remain in the main project folder.
