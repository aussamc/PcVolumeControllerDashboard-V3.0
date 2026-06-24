using System;
#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
#endif

namespace PcVolumeControllerDashboard.App.Platform;

/// <summary>
/// Windows-only OS integration: a single-instance guard (with bring-existing-to-front)
/// and run-on-login registry sync. Every member is a safe no-op on non-Windows
/// builds — the Linux/macOS equivalents (autostart entries, their own single-instance
/// mechanisms) land with those platform layers. Mirrors the WPF host's App + the
/// ApplyStartupSetting registry write.
/// </summary>
internal static class WindowsGlue
{
    /// <summary>Window-title prefix used to find the existing instance's main window.</summary>
    public const string AppWindowTitlePrefix = "PC Volume Controller Dashboard";

#if WINDOWS
    private const string SingleInstanceMutexName = "Global\\PcVolumeControllerDashboard_9F4A2C1B";
    private const string StartupRegistryName = "PcVolumeControllerDashboard";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int SwRestore = 9;

    private static Mutex? _instanceMutex;

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Returns true if this is the first instance (and holds the mutex); false if
    /// another instance is already running.
    /// </summary>
    public static bool TryAcquireSingleInstance()
    {
        _instanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
        return isFirst;
    }

    public static void ReleaseSingleInstance()
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    /// <summary>Restores and foregrounds the already-running instance's main window.</summary>
    public static void BringExistingInstanceToFront()
    {
        try
        {
            IntPtr found = IntPtr.Zero;
            uint currentPid = (uint)Environment.ProcessId;

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == currentPid) return true; // skip our own (second) process

                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, sb.Capacity);
                if (sb.ToString().StartsWith(AppWindowTitlePrefix, StringComparison.Ordinal))
                {
                    found = hWnd;
                    return false; // stop
                }
                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
            {
                ShowWindow(found, SwRestore);
                SetForegroundWindow(found);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Adds or removes the HKCU Run entry so the app launches at login. Idempotent.
    /// </summary>
    public static void ApplyRunOnStartup(bool enabled, Action<string>? log = null)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null)
            {
                log?.Invoke("Could not open Windows startup registry key.");
                return;
            }

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath) || !System.IO.File.Exists(exePath))
                {
                    log?.Invoke("Could not determine app path for run-on-startup.");
                    return;
                }
                key.SetValue(StartupRegistryName, $"\"{exePath}\"");
                log?.Invoke($"Run-on-startup enabled: {exePath}");
            }
            else
            {
                key.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
                log?.Invoke("Run-on-startup disabled.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Run-on-startup error: {ex.Message}");
        }
    }
#else
    public static bool TryAcquireSingleInstance() => true;
    public static void ReleaseSingleInstance() { }
    public static void BringExistingInstanceToFront() { }
    public static void ApplyRunOnStartup(bool enabled, Action<string>? log = null) { }
#endif
}
