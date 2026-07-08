using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Platform-neutral seam over "is the PC actively in use", so the host can put the
/// controller to sleep when the machine locks / suspends / goes idle and wake it on
/// resume — the README's documented "Auto sleep/wake" feature. The Windows impl uses
/// <c>Microsoft.Win32.SystemEvents</c> (session lock/unlock + power suspend/resume)
/// plus a <c>GetLastInputInfo</c> idle poll; other platforms get
/// <see cref="NullPcActivityMonitor"/> — Wayland/X11 have no cross-desktop-environment
/// idle/lock API (the same reason global hotkeys are Null on Linux), and macOS is
/// deferred.
///
/// The implementation owns the combined-state logic and only raises a transition:
/// <see cref="SleepRequested"/> fires once when the PC first becomes inactive (and
/// again only if the reason changes, e.g. idle → locked), and
/// <see cref="WakeRequested"/> fires once when it becomes active again. Events may
/// fire on a background thread; subscribers must marshal to the UI thread.
/// </summary>
public interface IPcActivityMonitor : IDisposable
{
    /// <summary>
    /// Raised when the PC becomes inactive. The argument is a short protocol-safe
    /// reason: <c>PC_LOCKED</c>, <c>PC_SUSPEND</c>, or <c>PC_IDLE</c>.
    /// </summary>
    event Action<string>? SleepRequested;

    /// <summary>
    /// Raised when the PC becomes active again after a sleep. The argument is a short
    /// reason (<c>PC_ACTIVE</c>).
    /// </summary>
    event Action<string>? WakeRequested;

    /// <summary>Begins monitoring. Idempotent — safe to call more than once.</summary>
    void Start();

    /// <summary>Stops monitoring and releases any OS event subscriptions.</summary>
    void Stop();
}
