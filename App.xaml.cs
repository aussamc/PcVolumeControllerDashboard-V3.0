using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace PcVolumeControllerDashboard;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = "Global\\PcVolumeControllerDashboard_9F4A2C1B";
    private static Mutex? _instanceMutex;

    // Mirrors the DllImport in MainWindow so App can lower timer resolution on crash.
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    // Win32 helpers for bringing an existing window to the foreground.
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private const int SwRestore = 9;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Single-instance guard ────────────────────────────────────────────
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            BringExistingInstanceToFront();
            _instanceMutex.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // ── Global crash handlers ────────────────────────────────────────────
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true; // prevent OS crash dialog
            HandleCrash("DispatcherUnhandledException", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                HandleCrash("UnhandledException", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            WriteCrashLog("UnobservedTaskException", args.Exception);
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
    }

    // ── Crash handling ───────────────────────────────────────────────────────

    private static void HandleCrash(string source, Exception ex)
    {
        // Lower timer resolution before any UI or file I/O so Windows restores
        // the 15.6 ms quantum even if the process ends abnormally.
        try { timeEndPeriod(1); } catch { }

        string logPath = WriteCrashLog(source, ex);

        // Show a polished error dialog with a Copy Details button.
        ShowCrashDialog(ex, logPath);

        Current?.Shutdown(1);
    }

    private static string WriteCrashLog(string source, Exception ex)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string logDir = Path.Combine(appData, "PcVolumeController", "logs");
            Directory.CreateDirectory(logDir);
            string path = Path.Combine(logDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {source}\r\n\r\n{ex}");
            return path;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ShowCrashDialog(Exception ex, string logPath)
    {
        try
        {
            string details = $"Source: {ex.GetType().FullName}\r\nMessage: {ex.Message}\r\n\r\n{ex.StackTrace}";

            string message =
                "PC Volume Controller Dashboard encountered an unexpected error and needs to close.\r\n\r\n" +
                $"Error: {ex.Message}";

            if (!string.IsNullOrEmpty(logPath))
            {
                message += $"\r\n\r\nA crash log has been saved to:\r\n{logPath}";
            }

            message += "\r\n\r\nClick OK to close, or Cancel to copy the crash details to the clipboard first.";

            System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
                message,
                "PC Volume Controller — Unexpected Error",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Error,
                System.Windows.MessageBoxResult.OK);

            if (result == System.Windows.MessageBoxResult.Cancel)
            {
                try { System.Windows.Clipboard.SetText(details); } catch { }
            }
        }
        catch
        {
            // If the dialog itself fails, fall through to shutdown.
        }
    }

    // ── Single-instance helpers ──────────────────────────────────────────────

    private static void BringExistingInstanceToFront()
    {
        try
        {
            IntPtr found = IntPtr.Zero;
            uint currentPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

            EnumWindows((hWnd, _) =>
            {
                // Skip the window belonging to this (second) process.
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid)
                {
                    return true;
                }

                var sb = new System.Text.StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().StartsWith("PC Volume Controller Dashboard", StringComparison.Ordinal))
                {
                    found = hWnd;
                    return false; // stop enumeration
                }

                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
            {
                ShowWindow(found, SwRestore);
                SetForegroundWindow(found);
            }
        }
        catch
        {
        }
    }
}
