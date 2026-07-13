namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Parsed command-line startup flags for the dashboard host. Pure and platform-
/// neutral so the parsing is unit-testable; the host applies the results.
///
/// Recognises:
/// <list type="bullet">
/// <item><c>--debug</c> — force-show the gated Debug tab for this session, regardless
///   of the <c>AdvancedDebugFeatures</c> setting.</item>
/// <item><c>--safe</c> — diagnostic launch: disable auto-connect, the reconnect loop,
///   and all audio-control writes (encoder/button/hotkey) so a misbehaving setup can
///   be inspected without the app driving the hardware or changing volumes.</item>
/// </list>
/// </summary>
public sealed record StartupOptions
{
    /// <summary>
    /// True if <c>--debug</c> was passed: force-show the Debug tab for this session
    /// even when <c>AdvancedDebugFeatures</c> is off.
    /// </summary>
    public bool ForceDebugTab { get; init; }

    /// <summary>
    /// True if <c>--safe</c> was passed: skip auto-connect / the reconnect loop and
    /// suppress every audio-control write for troubleshooting.
    /// </summary>
    public bool SafeMode { get; init; }

    private const string DebugFlag = "--debug";
    private const string SafeFlag = "--safe";

    /// <summary>
    /// Parses process arguments into a <see cref="StartupOptions"/>. Case-insensitive;
    /// unrecognised arguments are ignored. Never throws.
    /// </summary>
    public static StartupOptions Parse(string[]? args)
    {
        bool debug = false;
        bool safe = false;
        if (args != null)
        {
            foreach (string arg in args)
            {
                string trimmed = arg?.Trim() ?? string.Empty;
                if (string.Equals(trimmed, DebugFlag, StringComparison.OrdinalIgnoreCase))
                    debug = true;
                else if (string.Equals(trimmed, SafeFlag, StringComparison.OrdinalIgnoreCase))
                    safe = true;
            }
        }

        return new StartupOptions { ForceDebugTab = debug, SafeMode = safe };
    }
}
