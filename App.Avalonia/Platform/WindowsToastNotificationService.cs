#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App.Platform;

/// <summary>
/// Windows <see cref="INotificationService"/> impl (parity item F6): a modern toast
/// via CommunityToolkit (<c>Microsoft.Toolkit.Uwp.Notifications</c>). Shown from the
/// tray/background even while the window is hidden and lands in the Action Center —
/// unlike Avalonia's in-window <c>WindowNotificationManager</c>, and without the
/// second-tray-icon a WinForms balloon would need.
///
/// Lives in the host (not Platform.Windows) because the WinRT toast API requires the
/// host's OS-versioned TFM (<c>net10.0-windows10.0.17763.0</c>); Platform.Windows
/// stays on plain <c>net10.0-windows</c>. Same "Windows impl in the host behind
/// #if WINDOWS" placement as <c>Platform/WindowsHotkeys.cs</c>. Best-effort: the
/// coordinator wraps <see cref="Show"/> in try/catch.
/// </summary>
public sealed class WindowsToastNotificationService : INotificationService
{
    public void Show(string title, string message)
    {
        new ToastContentBuilder()
            .AddText(title)
            .AddText(message)
            .Show();
    }
}
#endif
