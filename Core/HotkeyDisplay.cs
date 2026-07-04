using System.Collections.Generic;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Pure, cross-platform formatting of a hotkey combo into a human string
/// ("Ctrl+Shift+K"). Replaces the WPF host's WinForms-<c>Keys</c>-based
/// <c>HotkeyBindingExtensions.ToDisplayString</c> so the Avalonia host has no
/// System.Windows.Forms dependency, and the naming is unit-tested.
///
/// Modifier flags follow the Win32 <c>RegisterHotKey</c> convention that the
/// settings already store: ALT=1, CTRL=2, SHIFT=4, WIN=8. Virtual-key codes are
/// standard Win32 VK codes.
/// </summary>
public static class HotkeyDisplay
{
    private const int ModAlt = 1;
    private const int ModControl = 2;
    private const int ModShift = 4;
    private const int ModWin = 8;

    /// <summary>Formats a binding, or "(unassigned)" when it isn't assigned.</summary>
    public static string Describe(HotkeyBinding binding) =>
        binding.IsAssigned ? Format(binding.Modifiers, binding.VirtualKey) : "(unassigned)";

    /// <summary>
    /// Formats modifier flags + a virtual-key code as "Win+Ctrl+Shift+Alt+Key"
    /// (only the present modifiers, in that stable order). Returns "(unassigned)"
    /// when <paramref name="virtualKey"/> is 0.
    /// </summary>
    public static string Format(int modifiers, int virtualKey)
    {
        if (virtualKey == 0) return "(unassigned)";

        var parts = new List<string>(4);
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        parts.Add(KeyName(virtualKey));
        return string.Join("+", parts);
    }

    /// <summary>Human name for a Win32 virtual-key code (falls back to a hex form).</summary>
    public static string KeyName(int vk)
    {
        // Letters A–Z (0x41–0x5A) and top-row digits 0–9 (0x30–0x39).
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();

        // Function keys F1–F24 (0x70–0x87).
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);

        // Numeric keypad 0–9 (0x60–0x69).
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);

        return vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x13 => "Pause",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2C => "PrintScreen",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num*",
            0x6B => "Num+",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => $"Key(0x{vk:X2})",
        };
    }
}
