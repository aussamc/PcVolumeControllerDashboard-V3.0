using Avalonia.Controls;
using PcVolumeControllerDashboard.Core;

namespace PcVolumeControllerDashboard.App;

public partial class MainWindow : Window
{
    // Parameterless ctor for the XAML runtime loader / visual designer. The DI
    // path below is what the app actually uses at runtime.
    public MainWindow()
    {
        InitializeComponent();
    }

    // SerialService is injected to prove the App.Avalonia → Core DI path end to
    // end. PR 1 only smoke-tests it; the retained connection + serial
    // orchestration are ported with the Setup tab.
    public MainWindow(SerialService serial) : this()
    {
        // Touch the injected service so the wiring is exercised at runtime, and
        // enumerate ports via Core — proves Core resolves and runs on this OS.
        _ = serial;
        Title = $"PC Volume Controller Dashboard — {SerialService.GetPortNames().Length} serial port(s)";
    }
}
