using Avalonia.Input;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Converts an Avalonia key press into the Win32-style modifier flags and
/// virtual-key code the settings (and <c>RegisterHotKey</c>) use. The Avalonia
/// replacement for the WPF picker's <c>KeyInterop.VirtualKeyFromKey</c> +
/// <c>Keyboard.Modifiers</c>.
/// </summary>
public static class HotkeyCapture
{
    /// <summary>True for a modifier key pressed on its own (ignored during capture).</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>Maps Avalonia <see cref="KeyModifiers"/> to the ALT=1/CTRL=2/SHIFT=4/WIN=8 flags.</summary>
    public static int ToModifierFlags(KeyModifiers modifiers)
    {
        int flags = 0;
        if ((modifiers & KeyModifiers.Alt) != 0) flags |= 1;
        if ((modifiers & KeyModifiers.Control) != 0) flags |= 2;
        if ((modifiers & KeyModifiers.Shift) != 0) flags |= 4;
        if ((modifiers & KeyModifiers.Meta) != 0) flags |= 8; // Meta = Windows key
        return flags;
    }

    /// <summary>
    /// Win32 virtual-key code for an Avalonia <see cref="Key"/>, or 0 when it has no
    /// stable mapping (the caller should then reject the capture).
    /// </summary>
    public static int ToVirtualKey(Key key)
    {
        // Consecutive ranges (Avalonia's Key enum mirrors WPF's ordering).
        if (key >= Key.A && key <= Key.Z) return 0x41 + (key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return 0x30 + (key - Key.D0);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return 0x60 + (key - Key.NumPad0);
        if (key >= Key.F1 && key <= Key.F24) return 0x70 + (key - Key.F1);

        return key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Return => 0x0D,
            Key.Pause => 0x13,
            Key.CapsLock => 0x14,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            Key.End => 0x23,
            Key.Home => 0x24,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.PrintScreen => 0x2C,
            Key.Insert => 0x2D,
            Key.Delete => 0x2E,
            Key.Multiply => 0x6A,
            Key.Add => 0x6B,
            Key.Subtract => 0x6D,
            Key.Decimal => 0x6E,
            Key.Divide => 0x6F,
            Key.OemSemicolon => 0xBA,
            Key.OemPlus => 0xBB,
            Key.OemComma => 0xBC,
            Key.OemMinus => 0xBD,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemTilde => 0xC0,
            Key.OemOpenBrackets => 0xDB,
            Key.OemPipe => 0xDC,
            Key.OemCloseBrackets => 0xDD,
            Key.OemQuotes => 0xDE,
            _ => 0,
        };
    }
}
