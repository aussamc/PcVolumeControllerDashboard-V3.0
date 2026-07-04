using System;
using System.Collections.Generic;

namespace PcVolumeControllerDashboard.Core;

/// <summary>One system-wide hotkey to register: an opaque id plus the Win32-style
/// modifier flags (ALT=1, CTRL=2, SHIFT=4, WIN=8) and virtual-key code.</summary>
public readonly record struct HotkeyRegistration(int Id, int Modifiers, int VirtualKey);

/// <summary>
/// Platform-neutral seam over system-wide (global) hotkeys. The Windows impl uses
/// <c>RegisterHotKey</c> on a dedicated message-only window; other platforms get
/// <see cref="NullGlobalHotkeyService"/> until their layers land (X11/Wayland on
/// Linux, CGEventTap on macOS — both deferred).
///
/// <see cref="HotkeyPressed"/> may fire on a background thread; subscribers must
/// marshal to the UI thread as needed.
/// </summary>
public interface IGlobalHotkeyService : IDisposable
{
    /// <summary>Fired with the registration <c>Id</c> when a registered hotkey is pressed.</summary>
    event Action<int>? HotkeyPressed;

    /// <summary>
    /// Replaces the entire set of registered hotkeys with <paramref name="registrations"/>
    /// (clears any previous set first). Safe to call repeatedly as bindings change.
    /// </summary>
    void RegisterAll(IReadOnlyList<HotkeyRegistration> registrations);

    /// <summary>Clears all currently registered hotkeys.</summary>
    void UnregisterAll();
}
