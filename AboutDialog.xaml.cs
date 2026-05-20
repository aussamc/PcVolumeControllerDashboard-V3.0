using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace PcVolumeControllerDashboard;

public partial class AboutDialog : Window
{
    public AboutDialog(string dashboardVersion, string firmwareVersion, string protocolVersion)
    {
        InitializeComponent();
        VersionTextBlock.Text = $"Version: v{dashboardVersion}";
        FirmwareTextBlock.Text = firmwareVersion == "Unknown"
            ? "Controller: Not connected"
            : $"Controller firmware: {firmwareVersion}  (protocol v{protocolVersion})";
    }

    private void GitHubHyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
