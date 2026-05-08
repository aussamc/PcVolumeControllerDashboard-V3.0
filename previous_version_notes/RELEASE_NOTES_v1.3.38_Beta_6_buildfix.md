# PC Volume Controller Dashboard v1.3.38 Beta 6 buildfix

## Fixed

- Fixed WPF build errors where `MouseEventArgs` was ambiguous between `System.Windows.Forms.MouseEventArgs` and `System.Windows.Input.MouseEventArgs`.
- Slider drag event handlers now explicitly use the WPF input event type.

## Version status

Dashboard version:       v1.3.38 Beta 6 buildfix
Required ESP32 protocol: v1.3.38 Beta 6
ESP32 firmware folder:   Computer_Volume_Controller_v1.3.38_Beta_6

No firmware reflash is required for this buildfix if Beta 6 firmware is already installed.
