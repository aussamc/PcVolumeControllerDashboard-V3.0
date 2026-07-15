using System;

namespace PcVolumeControllerDashboard.Core;

/// <summary>
/// Platform-neutral seam for raising a desktop notification that's visible even when
/// the dashboard is minimised to the tray (parity item F6). Selected per-OS the same
/// way as the audio backend / activity monitor: a Windows toast impl on the
/// <c>-windows</c> TFM, a <c>notify-send</c> impl on Linux, and
/// <see cref="NullNotificationService"/> on macOS (deferred) or wherever no
/// notification mechanism is available.
///
/// Implementations must be best-effort — a notification failing to show (no
/// mechanism installed, permission denied) must never throw into the caller. The
/// host coordinator also gates every call on the user's "tray notifications" setting.
/// </summary>
public interface INotificationService
{
    /// <summary>Shows a notification with a short title and body. Best-effort.</summary>
    void Show(string title, string message);

    /// <summary>
    /// Raised when the user clicks/activates a shown notification, so the host can bring
    /// the dashboard to the foreground. May fire on a background thread — subscribers must
    /// marshal to the UI thread themselves. Impls with no click-through support (Null, and
    /// Linux <c>notify-send</c>) simply never raise it.
    /// </summary>
    event Action? Activated;
}
