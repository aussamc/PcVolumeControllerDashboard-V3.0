using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// No-op <see cref="INotificationService"/> for platforms without a notification
/// mechanism (macOS is deferred), so the host can wire the seam unconditionally.
/// </summary>
public sealed class NullNotificationService : INotificationService
{
    public void Show(string title, string message) { }

    /// <summary>Never raised — this impl shows nothing to click.</summary>
    public event Action? Activated { add { } remove { } }
}
