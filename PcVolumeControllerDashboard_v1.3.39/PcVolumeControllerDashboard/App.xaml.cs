using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace PcVolumeControllerDashboard;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog("DispatcherUnhandledException", args.Exception);
            args.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteCrashLog("UnhandledException", ex);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("UnobservedTaskException", args.Exception);
        };
    }

    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string logDirectory = Path.Combine(appData, "PcVolumeController", "logs");
            Directory.CreateDirectory(logDirectory);
            string path = Path.Combine(logDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {source}\n\n{ex}");
        }
        catch
        {
        }
    }
}
