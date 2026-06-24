namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure translations between dashboard-domain constants and the on-the-wire
/// protocol strings sent to the ESP32 (STATE / CHSTATE / OLEDCFG / DISPMODE).
/// Centralised and unit-tested so every host (WPF and Avalonia) formats outbound
/// state identically.
/// </summary>
public static class ProtocolMapping
{
    /// <summary>Maps a global OLED display mode to its protocol value (defaults to APP_VOLUME).</summary>
    public static string DisplayModeToProtocol(string? dashboardMode) => dashboardMode switch
    {
        DisplayModes.LargeVolume     => ProtocolCommands.DisplayModeLargeVolume,
        DisplayModes.MuteStatus      => ProtocolCommands.DisplayModeMuteStatus,
        DisplayModes.AppOrDeviceName => ProtocolCommands.DisplayModeAppName,
        DisplayModes.BarPercent      => ProtocolCommands.DisplayModeBarPercent,
        _                            => ProtocolCommands.DisplayModeAppVolume,
    };

    /// <summary>
    /// Maps a per-channel OLED display mode to its protocol value. An empty/blank
    /// input maps to empty output — the firmware interprets that as "use the
    /// global mode" for the channel.
    /// </summary>
    public static string ChannelDisplayModeToProtocol(string? dashboardMode)
    {
        if (string.IsNullOrEmpty(dashboardMode)) return string.Empty;
        return DisplayModeToProtocol(dashboardMode);
    }

    /// <summary>
    /// Maps a connected-idle action to its protocol value: a "DimToNN" action
    /// becomes "DIM_NN" (clamped to 10–70); anything else (including Off) becomes
    /// "OFF".
    /// </summary>
    public static string IdleActionToProtocol(string? action)
    {
        if (!string.IsNullOrEmpty(action) &&
            action.StartsWith("DimTo", System.StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(action.AsSpan(5), out int dimPercent))
        {
            dimPercent = System.Math.Clamp(dimPercent, 10, 70);
            return $"DIM_{dimPercent}";
        }

        return "OFF";
    }

    /// <summary>
    /// Sanitises a label for the protocol: strips commas (the field separator),
    /// trims, caps at 18 characters, and substitutes "Unknown" for an empty
    /// result. Matches the WPF host's MakeProtocolSafeLabel.
    /// </summary>
    public static string MakeProtocolSafeLabel(string? label)
    {
        string cleaned = (label ?? string.Empty).Replace(',', ' ').Trim();
        if (cleaned.Length == 0) return "Unknown";
        return cleaned.Length <= 18 ? cleaned : cleaned[..18];
    }
}
