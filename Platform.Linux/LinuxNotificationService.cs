using System.Diagnostics;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.Platform.Linux;

/// <summary>
/// Linux desktop-notification impl (parity item F6): shells out to <c>notify-send</c>
/// (libnotify), the lowest-common-denominator way to raise a desktop notification
/// across GNOME/KDE and Wayland/X11 — matching this layer's existing shell-out
/// convention (<c>pw-dump</c>/<c>wpctl</c> in <see cref="PipeWireAudioBackend"/>).
///
/// Best-effort: if <c>notify-send</c> isn't installed the launch throws and the
/// coordinator swallows it. <c>ArgumentList</c> passes the title/body as single
/// unquoted arguments so their spaces/punctuation aren't re-parsed by a shell.
/// </summary>
public sealed class LinuxNotificationService : INotificationService
{
    public void Show(string title, string message)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "notify-send",
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--app-name=PC Volume Controller");
        psi.ArgumentList.Add(title);
        psi.ArgumentList.Add(message);
        Process.Start(psi);
    }
}
