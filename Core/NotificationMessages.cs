namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure formatters for desktop-notification body text (parity item F6). Kept in Core
/// so the wording is unit-testable and shared by every platform notifier. Titles are
/// short constants at the call sites; these format the variable body lines.
/// </summary>
public static class NotificationMessages
{
    /// <summary>Body for the "controller connected" notification.</summary>
    public static string Connected(string? protocol, string? chipId)
    {
        string proto = string.IsNullOrWhiteSpace(protocol) ? "unknown" : protocol!.Trim();
        string chip = string.IsNullOrWhiteSpace(chipId) ? "(none)" : chipId!.Trim();
        return $"Connected — protocol {proto}, chip {chip}.";
    }

    /// <summary>Body for the "controller disconnected" notification.</summary>
    public static string Disconnected() => "The controller was disconnected.";

    /// <summary>Body for the "update available" notification (v3.19 auto-updater).</summary>
    public static string UpdateAvailable(string? version)
    {
        string v = string.IsNullOrWhiteSpace(version) ? "A new version" : $"Version {version!.Trim()}";
        return $"{v} of the dashboard is available.";
    }
}
