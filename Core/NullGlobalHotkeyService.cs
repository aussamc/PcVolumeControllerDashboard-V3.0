using System;
using System.Collections.Generic;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// No-op <see cref="IGlobalHotkeyService"/> used where global hotkeys aren't yet
/// supported (Linux/macOS) so the host can wire the seam unconditionally.
/// </summary>
public sealed class NullGlobalHotkeyService : IGlobalHotkeyService
{
    public event Action<int>? HotkeyPressed { add { } remove { } }

    public void RegisterAll(IReadOnlyList<HotkeyRegistration> registrations) { }

    public void UnregisterAll() { }

    public void Dispose() { }
}
