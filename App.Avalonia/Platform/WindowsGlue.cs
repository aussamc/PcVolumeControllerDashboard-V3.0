using System;
#if WINDOWS
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using PcVolumeControllerDashboard.Core;
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
    // Task Manager / Settings > Startup Apps records an enable/disable flag per Run
    // entry here. An odd first byte means "disabled" — and while it's set, Windows
    // ignores the Run value entirely, so our entry silently never launches.
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
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
    /// When <paramref name="userInitiated"/> is true (the Setup checkbox / wizard
    /// toggle), enabling also clears any "disabled" state Task Manager / Startup
    /// Apps recorded for the entry — otherwise the Run value exists but Windows
    /// ignores it and the app never launches at login. The passive per-launch
    /// re-sync leaves a Task-Manager disable in place (the user set it in the OS;
    /// don't fight it silently) and just logs the conflict.
    ///
    /// The passive re-sync also refuses to record a transient process location (see
    /// <see cref="StartupPathPolicy"/>): an updater-staged build running from temp, or
    /// a dev build running from bin\Debug, would otherwise overwrite a perfectly good
    /// installed-path entry with one that stops resolving as soon as that folder is
    /// swept or rebuilt — leaving the app silently not starting at the next logon. A
    /// user-initiated toggle still writes whatever it is running from (an explicit
    /// action shouldn't silently do nothing) but warns when the location looks
    /// transient.
    /// </summary>
    public static void ApplyRunOnStartup(bool enabled, Action<string>? log = null, bool userInitiated = false)
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

                if (StartupPathPolicy.IsTransientLocation(exePath, System.IO.Path.GetTempPath()))
                {
                    if (!userInitiated)
                    {
                        log?.Invoke("Run-on-startup left unchanged: this build is running from a " +
                                    $"temporary location ({exePath}) that won't exist at the next " +
                                    "logon, so the existing startup entry was kept.");
                        return;
                    }

                    log?.Invoke($"Run-on-startup set to a temporary location ({exePath}). It will stop " +
                                "working once that folder is cleaned up — re-enable it from the " +
                                "installed copy to point it somewhere permanent.");
                }

                key.SetValue(StartupRegistryName, $"\"{exePath}\"");
                log?.Invoke($"Run-on-startup enabled: {exePath}");

                if (IsStartupEntryDisabledByWindows())
                {
                    if (userInitiated)
                    {
                        ClearStartupApprovedState();
                        log?.Invoke("Cleared the Windows Startup-Apps 'disabled' flag for the startup entry.");
                    }
                    else
                    {
                        log?.Invoke("Run-on-startup is enabled in settings but the entry is DISABLED in " +
                                    "Windows Startup Apps (Task Manager > Startup). Re-enable it there, or " +
                                    "toggle 'Start program at login' off and on in Setup.");
                    }
                }
            }
            else
            {
                key.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
                // Drop the stale approved-state record too so a future enable
                // starts from a clean (enabled-by-default) slate.
                ClearStartupApprovedState();
                log?.Invoke("Run-on-startup disabled.");
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Run-on-startup error: {ex.Message}");
        }
    }

    /// <summary>True if Task Manager / Startup Apps has recorded our entry as disabled.</summary>
    private static bool IsStartupEntryDisabledByWindows()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);
            return key?.GetValue(StartupRegistryName) is byte[] { Length: > 0 } data && (data[0] & 0x01) != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Removes the StartupApproved record for our entry. No record = enabled, so
    /// deleting is the format-agnostic way to clear a "disabled" flag.
    /// </summary>
    private static void ClearStartupApprovedState()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
            key?.DeleteValue(StartupRegistryName, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }
#else
    public static bool TryAcquireSingleInstance() => true;
    public static void ReleaseSingleInstance() { }
    public static void BringExistingInstanceToFront() { }
    public static void ApplyRunOnStartup(bool enabled, Action<string>? log = null, bool userInitiated = false) { }
#endif
}
