using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PcVolumeControllerDashboard.App.Services;

/// <summary>Severity of a log line, ordered low → high for threshold comparison.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

/// <summary>
/// File logger for the Avalonia host. Writes level-tagged, categorised lines to the
/// per-OS user config dir's <c>logs</c> folder (alongside the WPF host's logs).
///
/// Standard-logging model: every line carries a <see cref="LogLevel"/>, a thread id, and
/// an optional component category, and lines below the current <see cref="MinimumLevel"/>
/// are dropped. The threshold is <see cref="LogLevel.Info"/> normally and
/// <see cref="LogLevel.Debug"/> when "advanced debug logging" is on (wired live via
/// <see cref="UseDebugWhen"/>), so the toggle is simply a verbosity threshold rather than
/// a separate logging path. The logs folder is bounded by both age and total size.
/// </summary>
public sealed class LogService
{
    // Startup prune: drop logs older than this, matching the WPF host's LogRetentionDays.
    private const int RetentionDays = 7;

    // ...and cap the folder's total size so a verbose (advanced) session can't let the
    // logs grow without bound even within the retention window.
    private const long MaxLogsDirBytes = 50L * 1024 * 1024; // 50 MB

    private readonly string _path;
    private readonly object _lock = new();

    // Live source of the "advanced logging on?" flag; wired at startup. Until then the
    // threshold defaults to Info, so early startup lines are never accidentally dropped.
    private Func<bool>? _debugEnabled;

    public LogService()
    {
        string baseDir = Path.GetDirectoryName(SettingsService.SettingsPath) ?? ".";
        string logsDir = Path.Combine(baseDir, "logs");
        try { System.IO.Directory.CreateDirectory(logsDir); } catch { /* best effort */ }
        _path = Path.Combine(logsDir, $"avalonia-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        CleanupOldLogs(logsDir);
    }

    /// <summary>Absolute path of the active log file.</summary>
    public string FilePath => _path;

    /// <summary>Directory the logs (and crash logs) live in.</summary>
    public string Directory => Path.GetDirectoryName(_path) ?? ".";

    /// <summary>
    /// Wires the live predicate that decides whether Debug-and-below lines are written.
    /// Call once at startup with the AdvancedDebugLogging setting accessor; read on every
    /// write, so toggling the setting takes effect immediately without a restart.
    /// </summary>
    public void UseDebugWhen(Func<bool> predicate) => _debugEnabled = predicate;

    /// <summary>Current minimum level written: Debug when advanced logging is on, else Info.</summary>
    public LogLevel MinimumLevel => (_debugEnabled?.Invoke() ?? false) ? LogLevel.Debug : LogLevel.Info;

    /// <summary>True when Debug-level lines are currently being written (advanced on).</summary>
    public bool IsDebugEnabled => MinimumLevel <= LogLevel.Debug;

    // ── Level-tagged API ─────────────────────────────────────────────────────────

    public void Trace(string message, string? category = null) => Write(LogLevel.Trace, category, message);
    public void Debug(string message, string? category = null) => Write(LogLevel.Debug, category, message);
    public void Info(string message, string? category = null) => Write(LogLevel.Info, category, message);
    public void Warn(string message, string? category = null) => Write(LogLevel.Warn, category, message);
    public void Error(string message, string? category = null) => Write(LogLevel.Error, category, message);

    /// <summary>
    /// Logs an error with its exception: the message + <c>ex.Message</c> at Error (always
    /// written), and the full exception (type + stack) at Debug (only when advanced is on).
    /// This keeps normal logs readable while making a shared advanced log fully diagnosable.
    /// </summary>
    public void Error(string message, Exception ex, string? category = null)
    {
        Write(LogLevel.Error, category, $"{message}: {ex.Message}");
        Write(LogLevel.Debug, category, ex.ToString());
    }

    /// <summary>
    /// Back-compat entry point: an untyped line is recorded at <see cref="LogLevel.Info"/>
    /// with no category. Prefer the level-tagged methods above for new call sites.
    /// </summary>
    public void Log(string message) => Write(LogLevel.Info, null, message);

    private void Write(LogLevel level, string? category, string message)
    {
        if (level < MinimumLevel)
            return;

        string cat = string.IsNullOrEmpty(category) ? string.Empty : $"[{category}] ";
        string line =
            $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz}  {Tag(level)} [t{Environment.CurrentManagedThreadId,-2}] {cat}{message}";

        lock (_lock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch { /* never let logging throw */ }
        }
        System.Diagnostics.Debug.WriteLine(line);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "?????",
    };

    /// <summary>
    /// Prunes the logs folder on startup: first by age (older than <see cref="RetentionDays"/>),
    /// then by total size (oldest-first until under <see cref="MaxLogsDirBytes"/>). Covers this
    /// host's logs (<c>avalonia-*</c>, <c>crash-*</c>) and the WPF host's (<c>dashboard-*</c>)
    /// since they share the folder; WPF's own cleanup only globs <c>dashboard-*</c>, so on a
    /// Linux/Avalonia-only box this is the only thing that sweeps it. Best-effort: never throws.
    /// </summary>
    private void CleanupOldLogs(string logsDir)
    {
        try
        {
            string[] patterns = { "avalonia-*.log", "crash-*.log", "dashboard-*.log" };
            DateTime cutoff = DateTime.Now.Date.AddDays(-RetentionDays);
            int deleted = 0;

            // Pass 1 — age.
            foreach (string pattern in patterns)
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

            // Pass 2 — total-size budget. Delete oldest-first (never the brand-new active
            // file) until the folder is under the cap.
            List<FileInfo> remaining = patterns
                .SelectMany(p => System.IO.Directory.EnumerateFiles(logsDir, p, SearchOption.TopDirectoryOnly))
                .Where(f => !string.Equals(f, _path, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTime)
                .ToList();

            long total = remaining.Sum(f => SafeLength(f));
            foreach (FileInfo f in remaining)
            {
                if (total <= MaxLogsDirBytes)
                    break;
                long len = SafeLength(f);
                try { f.Delete(); total -= len; deleted++; }
                catch { /* skip locked/missing */ }
            }

            if (deleted > 0)
                Info($"Log cleanup: removed {deleted} old/oversized file(s).", "Log");
        }
        catch { /* logs dir missing or inaccessible — nothing to clean */ }
    }

    private static long SafeLength(FileInfo f)
    {
        try { return f.Length; }
        catch { return 0; }
    }
}
