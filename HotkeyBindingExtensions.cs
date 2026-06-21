using Forms = System.Windows.Forms;

namespace PcVolumeControllerDashboard;

/// <summary>
/// WPF/WinForms-specific display helpers for the platform-agnostic
/// <see cref="HotkeyBinding"/>. Key naming relies on System.Windows.Forms.Keys,
/// so this lives in the Windows host rather than Core.
/// </summary>
internal static class HotkeyBindingExtensions
{
    public static string ToDisplayString(this HotkeyBinding binding)
    {
        if (!binding.IsAssigned) return "(unassigned)";
        var parts = new System.Collections.Generic.List<string>();
        if ((binding.Modifiers & 8) != 0) parts.Add("Win");
        if ((binding.Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & 4) != 0) parts.Add("Shift");
        if ((binding.Modifiers & 1) != 0) parts.Add("Alt");
        string keyName = ((Forms.Keys)binding.VirtualKey).ToString();
        parts.Add(keyName);
        return string.Join("+", parts);
    }
}
