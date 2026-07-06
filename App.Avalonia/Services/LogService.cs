using System;
using System.IO;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>
/// Minimal file logger for the Avalonia host. Writes timestamped lines to the
/// per-OS user config dir's <c>logs</c> folder (alongside the WPF host's logs),
/// mirroring the WPF host's diagnostics so hardware issues can be inspected.
/// </summary>
public sealed class LogService
{
    // Delete logs older than this many days on startup, matching the WPF host's
    // LogRetentionDays. Without this the folder grows unbounded — one
    // avalonia-*.log is written per launch and nothing ever pruned them.
    private const int RetentionDays = 7;

    private readonly string _path;
    private readonly object _lock = new();

    public LogService()
    {
        string baseDir = Path.GetDirectoryName(SettingsService.SettingsPath) ?? ".";
        string logsDir = Path.Combine(baseDir, "logs");
        try { System.IO.Directory.CreateDirectory(logsDir); } catch { /* best effort */ }
        _path = Path.Combine(logsDir, $"avalonia-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        CleanupOldLogs(logsDir);
    }

    /// <summary>
    /// Prunes log files older than <see cref="RetentionDays"/> so the logs folder
    /// stays bounded. Covers this host's own logs (<c>avalonia-*</c>, <c>crash-*</c>)
    /// and the WPF host's (<c>dashboard-*</c>) since they share the folder. WPF's own
    /// cleanup only globs <c>dashboard-*</c>, so on a Linux/Avalonia-only box this is
    /// the only thing that sweeps the folder. Best-effort: never throws.
    /// </summary>
    private void CleanupOldLogs(string logsDir)
    {
        try
        {
            DateTime cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            int deleted = 0;

            foreach (string pattern in new[] { "avalonia-*.log", "crash-*.log", "dashboard-*.log" })
            foreach (string file in System.IO.Directory.EnumerateFiles(logsDir, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTime(file).Date < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch { /* file locked or already gone — skip it */ }
            }

            if (deleted > 0)
                Log($"Log cleanup: removed {deleted} file(s) older than {RetentionDays} days.");
        }
        catch { /* logs dir missing or inaccessible — nothing to clean */ }
    }

    /// <summary>Absolute path of the active log file.</summary>
    public string FilePath => _path;

    public void Log(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch { /* never let logging throw */ }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }
}
