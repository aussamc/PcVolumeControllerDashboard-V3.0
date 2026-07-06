using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PcVolumeControllerDashboard.App.Services;

namespace PcVolumeControllerDashboard.App;

/// <summary>
/// Process-wide crash handling for the Avalonia host, mirroring the WPF host's
/// <c>App.xaml.cs</c>: an unhandled exception writes a timestamped crash log to the
/// per-OS config dir's <c>logs</c> folder and shows a friendly error dialog with a
/// "copy details" option — instead of the process vanishing with no trace, which is
/// especially opaque when launched from a desktop icon with no attached console.
///
/// The crash <em>log</em> is the guaranteed deliverable (written synchronously on
/// whatever thread faulted); the dialog is best-effort, since a hard background-thread
/// crash may be tearing the runtime down before a window can render.
/// </summary>
public static class CrashHandler
{
    private static LogService? _log;

    // Ensures the fatal path runs once: a cascade of exceptions during teardown
    // shouldn't stack dialogs or re-enter the shutdown.
    private static int _fatalHandled;

    /// <summary>Wires the process-wide exception handlers. Call once at startup.</summary>
    public static void Install(LogService log)
    {
        _log = log;

        // UI-thread exceptions — Avalonia's analogue of WPF's
        // DispatcherUnhandledException. Marking Handled keeps the app alive long
        // enough to show the dialog before we shut down.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            HandleFatal("DispatcherUnhandledException", e.Exception);
        };

        // Any-thread fatal exceptions. The runtime is usually terminating here, so
        // this is best-effort: get the crash log written before the process dies.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                HandleFatal("AppDomainUnhandledException", ex);
        };

        // Faulted tasks whose exception was never observed — non-fatal, so record
        // it and carry on rather than letting it escalate at finalization.
        TaskScheduler_UnobservedTaskException();
    }

    private static void TaskScheduler_UnobservedTaskException() =>
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            string path = WriteCrashLog("UnobservedTaskException", e.Exception);
            _log?.Log($"Unobserved task exception (logged{(path.Length > 0 ? $" to {Path.GetFileName(path)}" : "")}): {e.Exception.Message}");
        };

    private static void HandleFatal(string source, Exception ex)
    {
        if (Interlocked.Exchange(ref _fatalHandled, 1) == 1) return;

        string logPath = WriteCrashLog(source, ex);
        _log?.Log($"FATAL {source}: {ex}");

        ShowDialogThenShutdown(ex, logPath);
    }

    /// <summary>Writes a standalone crash log; returns its path (empty on failure).</summary>
    private static string WriteCrashLog(string source, Exception ex)
    {
        try
        {
            string dir = _log?.Directory ?? Path.GetTempPath();
            System.IO.Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {source}{Environment.NewLine}{Environment.NewLine}{ex}");
            return path;
        }
        catch
        {
            return string.Empty; // logging must never throw from a crash handler
        }
    }

    private static void ShowDialogThenShutdown(Exception ex, string logPath)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                // On the UI thread (a Dispatcher exception): show the dialog now and
                // shut down when the user dismisses it.
                ShowCrashWindow(ex, logPath, Shutdown);
                return;
            }

            // Faulted on a background thread: hand the dialog to the UI thread and
            // block this (dying) thread until it's dismissed, so the window has a
            // chance to render before the process exits. Bounded so teardown can't
            // hang forever if the dispatcher is already gone.
            using var dismissed = new ManualResetEventSlim(false);
            Dispatcher.UIThread.Post(() =>
            {
                try { ShowCrashWindow(ex, logPath, () => dismissed.Set()); }
                catch { dismissed.Set(); }
            });
            dismissed.Wait(TimeSpan.FromSeconds(30));
            Shutdown();
        }
        catch
        {
            Shutdown();
        }
    }

    private static void ShowCrashWindow(Exception ex, string logPath, Action onDismiss)
    {
        string details = $"Source: {ex.GetType().FullName}{Environment.NewLine}" +
                         $"Message: {ex.Message}{Environment.NewLine}{Environment.NewLine}{ex.StackTrace}";

        string message =
            "PC Volume Controller Dashboard hit an unexpected error and needs to close." +
            Environment.NewLine + Environment.NewLine + $"Error: {ex.Message}";
        if (logPath.Length > 0)
            message += Environment.NewLine + Environment.NewLine + "A crash log was saved to:" +
                       Environment.NewLine + logPath;

        var window = new Window
        {
            Title = "PC Volume Controller — Unexpected Error",
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            MinWidth = 380,
            MaxWidth = 620,
        };

        var copyButton = new Button { Content = "Copy details", MinWidth = 110 };
        copyButton.Click += async (_, _) =>
        {
            try { if (window.Clipboard is { } c) await c.SetTextAsync(details); } catch { /* best-effort */ }
        };

        var closeButton = new Button { Content = "Close", MinWidth = 84, IsDefault = true, IsCancel = true };
        closeButton.Click += (_, _) => window.Close();

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Unexpected error", FontWeight = FontWeight.SemiBold, FontSize = 16 },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { copyButton, closeButton },
                },
            },
        };

        // Whether dismissed via button, Esc, or the window chrome, run the follow-up
        // (shutdown / unblock the waiting crash thread) exactly once.
        window.Closed += (_, _) => onDismiss();
        window.Show();
    }

    private static void Shutdown()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                Dispatcher.UIThread.Post(() => desktop.Shutdown(1));
            else
                Environment.Exit(1);
        }
        catch
        {
            Environment.Exit(1);
        }
    }
}
