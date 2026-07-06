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
    private readonly string _path;
    private readonly object _lock = new();

    public LogService()
    {
        string baseDir = Path.GetDirectoryName(SettingsService.SettingsPath) ?? ".";
        string logsDir = Path.Combine(baseDir, "logs");
        try { System.IO.Directory.CreateDirectory(logsDir); } catch { /* best effort */ }
        _path = Path.Combine(logsDir, $"avalonia-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    /// <summary>Absolute path of the active log file.</summary>
    public string FilePath => _path;

    /// <summary>Directory the logs (and crash logs) live in.</summary>
    public string Directory => Path.GetDirectoryName(_path) ?? ".";

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
