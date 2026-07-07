using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// No-op <see cref="IPcActivityMonitor"/> for platforms without a cross-desktop
/// idle/lock/suspend API (Linux/Wayland, macOS deferred), so the host can wire the
/// seam unconditionally. Never raises sleep/wake — on those platforms the controller
/// follows its own firmware idle timeout instead.
/// </summary>
public sealed class NullPcActivityMonitor : IPcActivityMonitor
{
    public event Action<string>? SleepRequested { add { } remove { } }
    public event Action<string>? WakeRequested { add { } remove { } }

    public void Start() { }

    public void Stop() { }

    public void Dispose() { }
}
