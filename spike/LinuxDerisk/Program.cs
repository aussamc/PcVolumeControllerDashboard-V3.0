using Avalonia;

namespace LinuxDerisk;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Headless mode: run the probes in the terminal (no display needed), so an
        // agent or an SSH session can run and self-verify the spike end-to-end.
        if (args.Contains("--headless"))
        {
            Environment.Exit(Probe.Run(args));
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UsePlatformDetect()
                  .LogToTrace();
}
