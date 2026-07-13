namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Parsed command-line startup flags for the dashboard host. Pure and platform-
/// neutral so the parsing is unit-testable; the host applies the results.
///
/// Currently recognises <c>--debug</c> (force-show the gated Debug tab for this
/// session, regardless of the <c>AdvancedDebugFeatures</c> setting). The N1
/// <c>--safe</c> diagnostic flag is a planned sibling parsed here when it lands.
/// </summary>
public sealed record StartupOptions
{
    /// <summary>
    /// True if <c>--debug</c> was passed: force-show the Debug tab for this session
    /// even when <c>AdvancedDebugFeatures</c> is off.
    /// </summary>
    public bool ForceDebugTab { get; init; }

    private const string DebugFlag = "--debug";

    /// <summary>
    /// Parses process arguments into a <see cref="StartupOptions"/>. Case-insensitive;
    /// unrecognised arguments are ignored. Never throws.
    /// </summary>
    public static StartupOptions Parse(string[]? args)
    {
        bool debug = false;
        if (args != null)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg?.Trim(), DebugFlag, StringComparison.OrdinalIgnoreCase))
                    debug = true;
            }
        }

        return new StartupOptions { ForceDebugTab = debug };
    }
}
