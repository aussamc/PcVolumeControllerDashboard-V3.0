using Avalonia;

namespace PcVolumeControllerDashboard.App;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance guard (Windows; no-op elsewhere): a second launch hands
        // focus to the running instance and exits rather than starting a rival that
        // would fight over the serial port and tray icon.
        if (!Platform.WindowsGlue.TryAcquireSingleInstance())
        {
            Platform.WindowsGlue.BringExistingInstanceToFront();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Platform.WindowsGlue.ReleaseSingleInstance();
        }
    }

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
