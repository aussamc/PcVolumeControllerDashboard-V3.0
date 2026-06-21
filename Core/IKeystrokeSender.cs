namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Platform seam for synthesizing a user-configured key combination — the basis
/// for the planned <c>SendHotkey</c> channel-button action (fire-and-forget,
/// app-agnostic: Discord push-to-talk, OBS scene switch, Teams mute, etc.).
///
/// Designed during Phase 0 so the cross-platform shape is fixed early; the
/// concrete implementations land later alongside the SendHotkey feature:
///   • Windows — SendInput (Win32)
///   • Linux   — uinput, or XTEST under X11
///   • macOS   — CGEventPost
///
/// The combination is described by a <see cref="HotkeyBinding"/>: a modifier
/// bitmask (<see cref="KeyModifiers"/>) plus a Win32 virtual-key code. The
/// virtual-key code is the persisted source of truth (it is what the hotkey
/// picker captures); non-Windows implementations translate it to their native
/// key representation.
/// </summary>
public interface IKeystrokeSender
{
    /// <summary>
    /// Synthesizes a press-and-release of the combo described by
    /// <paramref name="binding"/>. Returns <c>false</c> if the binding is
    /// unassigned or synthesis failed. Implementations must not throw.
    /// </summary>
    bool SendHotkey(HotkeyBinding binding);
}

/// <summary>
/// Modifier bitmask used by <see cref="HotkeyBinding.Modifiers"/>. Values match
/// the Win32 <c>MOD_*</c> constants used by RegisterHotKey so the persisted
/// settings format is unchanged across platforms.
/// </summary>
[Flags]
public enum KeyModifiers
{
    None    = 0,
    Alt     = 1,
    Control = 2,
    Shift   = 4,
    Win     = 8,
}

/// <summary>
/// Safe default that performs no synthesis. Lets a host wire up dependency
/// injection on a platform whose real <see cref="IKeystrokeSender"/> is not yet
/// implemented, without null checks at every call site.
/// </summary>
public sealed class NullKeystrokeSender : IKeystrokeSender
{
    public bool SendHotkey(HotkeyBinding binding) => false;
}
