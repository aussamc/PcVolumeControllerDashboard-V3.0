namespace PcVolumeControllerDashboard.Core;

public static class ThemeModes
{
    public const string FollowSystem = "FollowSystem";
    public const string Light = "Light";
    public const string Dark = "Dark";
}

public static class ChannelButtonActions
{
    public const string SelectNextChannel = "SelectNextChannel";
    public const string ToggleAssignedMute = "ToggleAssignedMute";
    public const string NoAction = "NoAction";
    public const string CycleNextProfile = "CycleNextProfile";
    public const string CycleOutputDevice = "CycleOutputDevice";
    public const string ApplyPreset1 = "ApplyPreset1";
    public const string ApplyPreset2 = "ApplyPreset2";
    public const string ApplyPreset3 = "ApplyPreset3";
    public const string MediaPlayPause = "MediaPlayPause";
    public const string MediaNextTrack = "MediaNextTrack";
    public const string MediaPrevTrack = "MediaPrevTrack";
    public const string MediaStop      = "MediaStop";

    public static bool IsValid(string? action)
    {
        return action is SelectNextChannel or ToggleAssignedMute or NoAction or CycleNextProfile or CycleOutputDevice
            or ApplyPreset1 or ApplyPreset2 or ApplyPreset3
            or MediaPlayPause or MediaNextTrack or MediaPrevTrack or MediaStop;
    }

    // Long press and double press support ToggleAssignedMute, NoAction, CycleNextProfile,
    // CycleOutputDevice, ApplyPreset*, and media key actions.
    public static bool IsValidLongPressAction(string? action)
    {
        return action is ToggleAssignedMute or NoAction or CycleNextProfile or CycleOutputDevice
            or ApplyPreset1 or ApplyPreset2 or ApplyPreset3
            or MediaPlayPause or MediaNextTrack or MediaPrevTrack or MediaStop;
    }

    public static bool IsValidDoublePressAction(string? action)
    {
        return action is ToggleAssignedMute or NoAction or CycleNextProfile or CycleOutputDevice
            or ApplyPreset1 or ApplyPreset2 or ApplyPreset3
            or MediaPlayPause or MediaNextTrack or MediaPrevTrack or MediaStop;
    }
}

public static class OledIdleActions
{
    public const string Off = "Off";
    public const string DimTo10 = "DimTo10";
    public const string DimTo20 = "DimTo20";
    public const string DimTo30 = "DimTo30";
    public const string DimTo40 = "DimTo40";
    public const string DimTo50 = "DimTo50";
    public const string DimTo60 = "DimTo60";
    public const string DimTo70 = "DimTo70";

    public static bool IsValid(string action)
    {
        return action is Off or DimTo10 or DimTo20 or DimTo30 or DimTo40 or DimTo50 or DimTo60 or DimTo70;
    }
}

public static class DisplayModes
{
    public const string AppNameAndVolume = "AppNameAndVolume";
    public const string LargeVolume = "LargeVolume";
    public const string MuteStatus = "MuteStatus";
    public const string AppOrDeviceName = "AppOrDeviceName";
    public const string BarPercent = "BarPercent";

    /// <summary>
    /// Returns true if <paramref name="mode"/> is a valid per-channel OLED display mode.
    /// Empty string is valid — it means "inherit the global default".
    /// </summary>
    public static bool IsValidChannelMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return true;
        return mode is AppNameAndVolume or LargeVolume or MuteStatus or AppOrDeviceName or BarPercent;
    }
}

public static class AudioBackendModes
{
    public const string Wasapi      = "WASAPI";
    public const string VoiceMeeter = "VoiceMeeter";

    public static bool IsValid(string? v) => v is Wasapi or VoiceMeeter;
}

public static class AccelerationPresets
{
    public const string None = "None";
    public const string Light = "Light";
    public const string Medium = "Medium";
    public const string Aggressive = "Aggressive";
    public const string Custom = "Custom";

    public static bool IsValid(string? v) => v is None or Light or Medium or Aggressive or Custom;
}

public static class SmoothingSpeed
{
    public const string Fast = "Fast";
    public const string Normal = "Normal";
    public const string Slow = "Slow";

    public static bool IsValid(string? v) => v is Fast or Normal or Slow;
}

public static class RebindFallbacks
{
    public const string ShowInactive = "ShowInactive";
    public const string DoNothing    = "DoNothing";

    public static bool IsValid(string? v) => v is ShowInactive or DoNothing;
}
